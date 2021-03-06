// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Diagnostics.Contracts;

namespace System.Text
{
    internal static partial class EncodingTable
    {
        internal static int GetCodePageFromName(string name)
        {
            if (name == null)
                throw new ArgumentNullException("name");
            Contract.EndContractBlock();

            return (int)NameToCodePageCache.Instance.GetOrAdd(name);
        }

        private sealed class NameToCodePageCache : ConcurrentUnifier<string, object>
        {
            public static readonly NameToCodePageCache Instance = new NameToCodePageCache();

            protected sealed override object Factory(string name)
            {
                return InternalGetCodePageFromName(name);
            }
        }

        unsafe private static int InternalGetCodePageFromName(string name)
        {
            int left = 0;
            int right = s_encodingNameIndices.Length - 2;
            int index;
            int result;

            Contract.Assert(s_encodingNameIndices.Length == s_codePagesByName.Length + 1);
            Contract.Assert(s_encodingNameIndices[s_encodingNameIndices.Length - 1] == s_encodingNames.Length);

            name = name.ToLowerInvariant();

            //Binary search the array until we have only a couple of elements left and then
            //just walk those elements.
            while ((right - left) > 3)
            {
                index = ((right - left) / 2) + left;

                Contract.Assert(index < s_encodingNameIndices.Length - 1);
                result = CompareOrdinal(name, s_encodingNames, s_encodingNameIndices[index], s_encodingNameIndices[index + 1] - s_encodingNameIndices[index]);
                if (result == 0)
                {
                    //We found the item, return the associated codePage.
                    return (s_codePagesByName[index]);
                }
                else if (result < 0)
                {
                    //The name that we're looking for is less than our current index.
                    right = index;
                }
                else
                {
                    //The name that we're looking for is greater than our current index
                    left = index;
                }
            }

            //Walk the remaining elements (it'll be 3 or fewer).
            for (; left <= right; left++)
            {
                Contract.Assert(left < s_encodingNameIndices.Length - 1);
                if (CompareOrdinal(name, s_encodingNames, s_encodingNameIndices[left], s_encodingNameIndices[left + 1] - s_encodingNameIndices[left]) == 0)
                {
                    return (s_codePagesByName[left]);
                }
            }

            // The encoding name is not valid.
            throw new ArgumentException(
                SR.Format(SR.Argument_EncodingNotSupported, name),
                "name");
        }

        private static int CompareOrdinal(string s1, string s2, int index, int length)
        {
            int count = s1.Length;
            if (count > length)
                count = length;

            int i = 0;
            while (i < count && s1[i] == s2[index + i])
                i++;

            if (i < count)
                return (int)(s1[i] - s2[index + i]);

            return s1.Length - length;
        }

        unsafe internal static string GetWebNameFromCodePage(int codePage)
        {
            return CodePageToWebNameCache.Instance.GetOrAdd(codePage);
        }

        private sealed class CodePageToWebNameCache : ConcurrentUnifier<int, string>
        {
            public static readonly CodePageToWebNameCache Instance = new CodePageToWebNameCache();

            protected sealed override string Factory(int codePage)
            {
                return GetNameFromCodePage(codePage, s_webNames, s_webNameIndices);
            }
        }

        unsafe internal static string GetEnglishNameFromCodePage(int codePage)
        {
            return CodePageToEnglishNameCache.Instance.GetOrAdd(codePage);
        }

        private sealed class CodePageToEnglishNameCache : ConcurrentUnifier<int, string>
        {
            public static readonly CodePageToEnglishNameCache Instance = new CodePageToEnglishNameCache();

            protected sealed override string Factory(int codePage)
            {
                return GetNameFromCodePage(codePage, s_englishNames, s_englishNameIndices);
            }
        }

        unsafe private static string GetNameFromCodePage(int codePage, string names, int[] indices)
        {
            string name;

            Contract.Assert(s_mappedCodePages.Length + 1 == indices.Length);
            Contract.Assert(indices[indices.Length - 1] == names.Length);

            //This is a linear search, but we probably won't be doing it very often.
            for (int i = 0; i < s_mappedCodePages.Length; i++)
            {
                if (s_mappedCodePages[i] == codePage)
                {
                    Contract.Assert(i < indices.Length - 1);

                    fixed (char* pChar = names)
                    {
                        name = new String(pChar, indices[i], indices[i + 1] - indices[i]);
                    }

                    return name;
                }
            }

            //Nope, we didn't find it.
            return null;
        }
    }
}
