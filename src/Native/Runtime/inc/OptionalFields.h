//
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

//
// Support for optional fields attached out-of-line to EETypes (or any other data structure for that matter).
// These should be used for attributes that exist for only a small subset of EETypes or are accessed only
// rarely. The idea is to avoid bloating the size of the most common EETypes and to move some of the colder
// data out-of-line to improve the density of the hot data. The basic idea is that the EEType contains a
// pointer to an OptionalFields structure (which may be NULL) and that structure contains a somewhat
// compressed version of the optional fields.
//
// For each OptionalFields instance we encode only the fields that are present so that the structure is as
// small as possible while retaining reasonable access costs.
//
// This implies some tricky tradeoffs:
//  * The more we compress the data the greater the access costs in terms of CPU.
//  * More effective compression schemes tend to lead to the payload data being unaligned. This itself can
//    result in overhead but on some architectures it's worse than that and the unaligned nature of the data
//    requires special handling in client code. Generally it would be more robust and clean not to leak out
//    such requirements to our callers. For small fields we can imagine copying the data into aligned storage
//    (and indeed that might be a natural part of the decompression process). It might be more problematic for
//    larger data items.
//
// In order to get the best of both worlds we employ a hybrid approach. Small values (typically single small
// integers) get encoded inline in a compressed format. Decoding them will automatically copy them into
// aligned storage. Larger values (such as complex data structures) will be stored out-of-line, naturally
// aligned and uncompressed (at least by this layer of the software). The entry in the optional field record
// will instead contain a reference to this out-of-line structure.
//
// Pointers are large (especially on 64-bit) and incur overhead in terms of base relocs and complexity (since
// the locations requiring relocs may not be aligned). To mitigate this we can encode references to these
// out-of-line records as deltas from a base address and by carefully ordering the layout of the out-of-line
// records we can share the same base address amongst multiple OptionalFields structures.
//
// Taking this to one end of the logical extreme we could store a single base address such as the module base
// address and encode all OptionalFields references as offsets from this; basically RVAs. This is cheap in the
// respect that we only need one base address (and associated reloc) but the majority of OptionalFields
// references will encode as fairly large deltas. As we'll touch on later our mechanism for compressing inline
// values in OptionalRecords is based on discarding insignificant leading zero bits; i.e. we encode small
// integers more effectively. So ideally we want to store multiple base addresses so we can lower the average
// encoding cost of the deltas.
//
// An additional concern is how these base addresses are located. Take the module base address example: we
// have no direct means of locating this based on an OptionalFields (or even the EEType that owns it). To
// obtain this value we're likely to have to perform some operation akin to a range lookup and there are
// interesting edge cases such as EETypes for generic types, which don't reside in modules.
//
// The approach taken here addresses several of the concerns above. The algorithm stores base addresses
// interleaved with the OptionalFields. They are located at well-known locations by aligning their addresses
// to a specific value (we can tune this but assume for the purposes of this explanation that the value is 64
// bytes). This implies that the address requiring a base reloc is always aligned plus it can be located
// cheaply from an OptionalFields address by masking off the low-order bits of that address.
//
// As OptionalFields are added any out-of-line data they reference is stored linearly in the same order (this
// does imply that all out-of-line records must live in the same section and thus must have the same access
// attributes). This provides locality: adjacent OptionalFields may encode deltas to different out-of-line
// records but since the out-of-line records are adjacent (or nearly so) as well, both deltas will be about
// the same size. Once we've filled in the space between stored base addresses (some padding might be needed
// near the end where a full OptionalField won't fit, but this should be small given good compression of
// OptionalFields) then we write out a new base address. This is chosen based on the first out-of-line record
// referenced by the next OptionalField (i.e. it will make the first delta zero and keep the subsequent ones
// small).
//
// Consider the following example where for the sake of simplicity we assume each OptionalFields structure has
// precisely one out-of-line reference:
//
//    +-----------------+                        Out-of-line Records
//    | Base Address    |----------------------> +--------------------+
//    +-----------------+                        | #1                 |
//    | OptionalFields  |                        +--------------------+
//    |   Record #1     |                        | #2                 |
//    |                 |                        |                    |
//    +-----------------+                        +--------------------+
//    | OptionalFields  |                        | #3                 |
//    |   Record #2     |         /------------> +--------------------+
//    |                 |        /               | #4                 |
//    +-----------------+       /                |                    |
//    | OptionalFields  |      /                 |                    |
//    |   Record #3     |     /                  +--------------------+
//    |                 |    /                   | #5                 |
//    +-----------------+   /                    |                    |
//    | Padding         |  /                     +--------------------+
//    +-----------------+ /                      :                    :
//    | Base Address    |-
//    +-----------------+
//    | OptionalFields  |
//    |   Record #4     |
//    |                 |
//    +-----------------+
//    | OptionalFields  |
//    |   Record #5     |
//    :                 :
//
// Each optional field uses the base address defined above it (at the lower memory address determined by
// masking off the alignment bits). No matter which out-of-line records they reference the deltas will be as
// small as we can make them.
//
// Lowering the alignment requirement introduces more base addresses and as a result also lowers the number of
// OptionalFields that share the same base address, leading to smaller encodings for out-of-line deltas. But
// at the same time it increases the number of pointers (and associated base relocs) that we must store.
// Additionally the compression of the deltas is not completely linear: certain ranges of delta magnitude will
// result in exactly the same storage being used when compressed. See the details of the delta encoding below
// to see how we can use this to our advantage when tuning the alignment of base addresses.
//
// We optimize the case where OptionalFields structs don't contain any out-of-line references. We collect
// those together and emit them in a single run with no interleaved base addresses.
//
// The OptionalFields record encoding itself is a byte stream representing one or more fields. The first byte
// is a field header: it contains a field type tag in the low-order 7 bits (giving us 128 possible field
// types) and the most significant bit indicates whether this is the last field of the structure. The field
// value (a 32-bit unsigned number) is encoded using the existing VarInt support which encodes the value in
// byte chunks taking between 1 and 5 bytes to do so.
//
// If the field value is out-of-line we decode the delta from the base address in much the same way as for
// inline field values. Before adding the delta to the base address, however, we scale it based on the natural
// alignment of the out-of-line data record it references. Since the out-of-line data is aligned on the same
// basis this scaling avoids encoding bits that will always be zero and thus allows us to reference a greater
// range of memory with a delta that encodes using less bytes.
//
// The value compression algorithm above gives us the non-linearity of compression referenced earlier. 32-bit
// values will encode in a given number of bytes based on the having a given number of significant
// (non-leading zero) bits:
//      5 bytes : 25 - 32 significant bits
//      4 bytes : 18 - 24 significant bits
//      3 bytes : 11 - 17 significant bits
//      2 bytes : 4 - 10 significant bits
//      1 byte  : 0 - 3 significant bits
//
// We can use this to our advantage when choosing an alignment at which to store base addresses. Assuming that
// most out-of-line data will have an alignment requirement of at least 4 bytes we note that the 2 byte
// encoding already gives us an addressable range of 2^10 * 4 == 4KB which is likely to be enough for the vast
// majority of cases. That is we can raise the granularity of base addresses until the average amount of
// out-of-line data addressed begins to approach 4KB which lowers the cost of storing the base addresses while
// not impacting the encoding size of deltas at all (there's no point in storing base addresses more
// frequently because it won't make the encodings of deltas any smaller).
//
// Trying to tune for one byte deltas all the time is probably not worth it. The addressability range (again
// assuming 4 byte alignment) is only 32 bytes and unless we start storing a lot of small data structures
// out-of-line tuning for this will involve placing the base addresses very frequently and our costs will be
// dominated by the size of the base address pointers and their relocs.
//

// Define enumeration of optional field tags.
enum OptionalFieldTag
{
#define DEFINE_INLINE_OPTIONAL_FIELD(_name, _type) OFT_##_name,
#define DEFINE_OUTLINE_OPTIONAL_FIELD(_name, _type) OFT_##_name,
#include "OptionalFieldDefinitions.h"
    OFT_Count // Number of field types we support
};

// Array that indicates whether a given field type is inline (true) or out-of-line (false).
static bool g_rgOptionalFieldTypeIsInline[OFT_Count] = {
#define DEFINE_INLINE_OPTIONAL_FIELD(_name, _type) true,
#define DEFINE_OUTLINE_OPTIONAL_FIELD(_name, _type) false,
#include "OptionalFieldDefinitions.h"
};

// Various random global constants we can tweak for performance tuning.
enum OptionalFieldConstants
{
    // Constants determining how often we interleave a "header" containing a base address for out-of-line
    // records into the stream of OptionalFields structures. These will occur at some power of 2 alignment of
    // memory address. The alignment must at least exceed that of a pointer (since we'll store a pointer in
    // the header and we need room for at least one OptionalFields record between each header). As the
    // alignment goes up we store less headers but may impose a larger one-time padding cost at the start of
    // the optional fields memory block as well as increasing the average encoding size for out-of-line record
    // deltas in each optional field record.
    //
    // Note that if you change these constants you must be sure to modify the alignment of the optional field
    // virtual section in ZapImage.cpp as well as ensuring the alignment of the containing physical section is
    // at least as high (this latter cases matters for the COFF output case only, when we're generating PE
    // images directly the physical section will get page alignment).
    OFC_HeaderAlignmentShift    = 7,
    OFC_HeaderAlignmentBytes    = 1 << OFC_HeaderAlignmentShift,
    OFC_HeaderAlignmentMask     = OFC_HeaderAlignmentBytes - 1,
};

// Support for some simple statistics gathering. Only used for performance tweaking and debugging of the
// algorithm so left disabled most of the time.
#ifdef BINDER
//#define FEATURE_OPTIONAL_FIELD_STATS 1
#endif
#ifdef FEATURE_OPTIONAL_FIELD_STATS
struct OptionalFieldStats
{
    UInt32      m_cOptionalFieldsStructs;
    UInt32      m_rgFieldCounts[OFT_Count];
    UInt32      m_rgSizeDist[8];
    UInt32      m_cHeaders;
    UInt32      m_cbPadding;
};
extern "C" OptionalFieldStats g_sOFStats;
#define OFS_COUNTER_INC(_name) g_sOFStats.m_c##_name++;
#else
#define OFS_COUNTER_INC(_name) 
#endif

#ifndef RHDUMP
typedef DPTR(class OptionalFields) PTR_OptionalFields;
typedef DPTR(PTR_OptionalFields) PTR_PTR_OptionalFields;
#endif // !RHDUMP


class OptionalFields
{
public:

    // Return the number of bytes necessary to encode the given integer.
    static UInt32 EncodingSize(UInt32 uiValue);

    // Encode the given field type and integer into the buffer provided (which is guaranteed to have enough
    // space). Update the pointer into the buffer to point just past the newly encoded bytes. Note that any
    // processing of the value for use with out-of-line records has already been performed; we're given the
    // raw value to encode.
    static void EncodeField(UInt8 ** ppFields, OptionalFieldTag eTag, bool fLastField, UInt32 uiValue);

#ifndef BINDER

    // Define accessors for each field type.
#define DEFINE_INLINE_OPTIONAL_FIELD(_name, _type)                       \
    _type Get##_name(_type defaultValue)                                 \
    {                                                                    \
    return (_type)GetInlineField(OFT_##_name, (UInt32)defaultValue); \
    }

#define DEFINE_OUTLINE_OPTIONAL_FIELD(_name, _type)                      \
    _type * Get##_name()                                                 \
    {                                                                    \
    return (_type*)GetOutlineField(OFT_##_name, _alignof(_type));    \
    }

#include "OptionalFieldDefinitions.h"

private:
    friend struct OptionalFieldsRuntimeBuilder;

    // Reads a field value (or the basis for an out-of-line record delta) starting from the first byte after
    // the field header. Advances the field location to the start of the next field.
    static OptionalFieldTag DecodeFieldTag(PTR_UInt8 * ppFields, bool *pfLastField);

    // Reads a field value (or the basis for an out-of-line record delta) starting from the first byte of a
    // field description. Advances the field location to the start of the next field.
    static UInt32 DecodeFieldValue(PTR_UInt8 * ppFields);

    UInt32 GetInlineField(OptionalFieldTag eTag, UInt32 uiDefaultValue);
#if 0
    // Currently not used for ProjectN, implementation needs to be "DAC'ified" if needed again
    void * GetOutlineField(OptionalFieldTag eTag, size_t cbValueAlignment);
#endif

#endif // !BINDER
};

#ifdef BINDER

class OptionalFieldsManager;

//
// Binder support for laying out OptionalFields structures. Note that this is relatively simple currently since
// none of the optional fields contain pointers. This will have to be revisited to support fixups and
// understand ZapNodes if pointers are introduced.
//
// Mostly this class just caches the fields declared for a particular OptionalFields and the resulting ZapNode
// once those fields have been encoded. The layout of OptionalFields in memory is handled by
// OptionalFieldsManager while the details of the encoding of each individual field are handled by the
// OptionalFields class itself.
//
class OptionalFieldsBuilder
{
public:
    // At construction time associate this build with a manager.
    OptionalFieldsBuilder(OptionalFieldsManager *pManager);

    // Once all fields have been added the ZapNode representing the OptionalFields structure can be retrieved
    // (and not before; this will be asserted in debug builds).
    ZapNode *GetNode();

    // Define setters for each field type.
#define DEFINE_INLINE_OPTIONAL_FIELD(_name, _type)  \
    void Add##_name(_type value)                    \
    {                                               \
        AddInlineField(OFT_##_name, (UInt32)value); \
    }

    // Note that the ZapBlob provided here must not have been placed since the OptionalFieldsManager will
    // place it later in a specific order within a special section.
#define DEFINE_OUTLINE_OPTIONAL_FIELD(_name, _type) \
    void Add##_name(ZapBlob * pValueBlob)           \
    {                                               \
        assert(!pValueBlob->IsPlaced());            \
        AddOutlineField(OFT_##_name, pValueBlob);   \
    }

#include "OptionalFieldDefinitions.h"

private:
    friend class OptionalFieldsManager;

    void AddInlineField(OptionalFieldTag eTag, UInt32 uiValue);
    void AddOutlineField(OptionalFieldTag eTag, ZapBlob * pValueBlob);

    // Cached field values specficied by one of the Add* methods above.
    struct OptionalField
    {
        bool                    m_fPresent;         // This field was added (we keep an array of all possible
                                                    // field types).
        UInt32                  m_uiOffset;         // Offset of image copy of the value from the base of the
                                                    // out-of-line record section. Set by manager during field
                                                    // encoding and used only for out-of-line fields.
        union
        {
            UInt32              m_uiValue;          // Inline value.
            ZapBlob *           m_pValueBlob;       // Out-of-line value.
        };
    };

    OptionalFieldsManager *     m_pManager;
    ZapNode *                   m_pNode;
    UInt32                      m_cFields;
    bool                        m_fContainsOutOfLineFields;
    OptionalField               m_rgFields[OFT_Count];
};

// The OptionalFieldsManager takes care of the layout of the two virtual sections used by OptionalFields: the
// OptionalFields themselves (possibly with base addresses interleaved) and the out-of-line data records.
class OptionalFieldsManager
{
public:
    OptionalFieldsManager(ZapImage * pZapImage);

    // Encode all the fields for one OptionalFields description cached in the given OptionalFieldsBuilder.
    ZapNode * EncodeFields(OptionalFieldsBuilder * pBuilder);

    // Place all the nodes created by this manager once all OptionalFields creation is complete.
    void Place();

private:
    // When we collect all the out-of-line records for later placement we also wish to cache the offset into
    // the virtual section at which each node lies. We could work this out from the nodes themselves but that
    // would be very expensive. This information is used to calculate the deltas between OptionalFields
    // out-of-line data references and the last out-of-line data record that was recorded as a base address.
    struct OutOfLineRecord
    {
        ZapNode *       m_pNode;
        UInt32          m_uiOffset;
    };

    // Given a builder calculate the size of the encoded version of the OptionalFields structure given the
    // current state of the OptionalFieldsManager (i.e. which base address is current etc.).
    UInt32 PlanEncoding(OptionalFieldsBuilder * pBuilder);

    // Actually encode the builder into an OptionalFields structure. The size of the encoding must have been
    // calculated by a previous call to PlanEncoding with no intervening manager state updates.
    ZapNode * PerformEncoding(OptionalFieldsBuilder * pBuilder, UInt32 cbEncoding);

    // Emit a new base address record using the given out-of-line data record as the new base. It's assumed
    // that sufficient padding has already been emitted such that the optional fields section is properly
    // aligned for this header record.
    void AddNewBaseAddressHeader(UInt32 idxBaseOutOfLineRecord);

    // Go through the builder looking for out-of-line records (it's assumed there is at least one if this is
    // called) adding copies of the data to the out-of-line records section. Returns the index of the first
    // record referenced by the builder which is the record that should be used as the base address if this is
    // the first OptionalFields emitted after a base address header.
    UInt32 AddOutOfLineRecords(OptionalFieldsBuilder * pBuilder);

    ZapImage *              m_pZapImage;
    vector<ZapNode*>        m_vecSimpleFields;              // OptionalFields without out-of-line records
    vector<ZapNode*>        m_vecComplexFields;             // OptionalFields with at least one out-of-line record
    vector<OutOfLineRecord> m_vecOutOfLineRecords;          // Out-of-line records referenced by above
    UInt32                  m_cbNextOutOfLineRecordOffset;  // Offset into section the next OOL record will occupy
    UInt32                  m_idxCurrentBaseOutOfLineRecord;// Index of OOL record currently being used as base
    UInt32                  m_cbFreeSpaceInCurrentGroup;    // Count of bytes left before next header is emitted
    bool                    m_fHeaderEmitted;               // Has at least one base address header been emitted?
};

#endif // BINDER

//
// Optional field encoder/decoder for dynamic types built at runtime
//
struct OptionalFieldsRuntimeBuilder
{
    void Decode(OptionalFields * pOptionalFields);

    UInt32 EncodingSize();

    UInt32 Encode(OptionalFields * pOptionalFields);

   struct OptionalField
    {
        bool                    m_fPresent;
        union
        {
            UInt32              m_uiValue;
            void *              m_pValueBlob;
        };
    };

    OptionalField               m_rgFields[OFT_Count];
};
