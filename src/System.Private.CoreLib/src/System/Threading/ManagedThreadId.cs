// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// =+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+
//
// Thread tracks managed thread IDs, recycling them when threads die to keep the set of
// live IDs compact.
//
// =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-

using System;
using System.Diagnostics;
using System.Runtime;

namespace System.Threading
{
    internal class ManagedThreadId
    {
        //
        // Binary tree used to keep track of active thread ids. Each node of the tree keeps track of 32 consecutive ids.
        // Implemented as immutable collection to avoid locks. Each modification creates a new top level node.
        // 
        class ImmutableIdDispenser
        {
            private readonly ImmutableIdDispenser _left; // Child nodes
            private readonly ImmutableIdDispenser _right;

            private readonly int _used; // Number of ids tracked by this node and all its childs
            private readonly int _size; // Maximum number of ids that can be tracked by this node and all its childs

            private readonly uint _bitmap; // Bitmap of ids tracked by this node

            private const int BitsPerNode = 32;

            private ImmutableIdDispenser(ImmutableIdDispenser left, ImmutableIdDispenser right, int used, int size, uint bitmap)
            {
                _left = left;
                _right = right;
                _used = used;
                _size = size;
                _bitmap = bitmap;

                CheckInvariants();
            }

            [Conditional("DEBUG")]
            private void CheckInvariants()
            {
                int actualUsed = 0;

                uint countBits = _bitmap;
                while (countBits != 0)
                {
                    actualUsed += (int)(countBits & 1);
                    countBits >>= 1;
                }

                if (_left != null)
                {
                    Debug.Assert(_left._size == ChildSize);
                    actualUsed += _left._used;
                }
                if (_right != null)
                {
                    Debug.Assert(_right._size == ChildSize);
                    actualUsed += _right._used;
                }

                Debug.Assert(actualUsed == _used);
                Debug.Assert(_used <= _size);
            }

            private int ChildSize
            {
                get
                {
                    Debug.Assert((_size / 2) >= (BitsPerNode / 2));
                    return (_size / 2) - (BitsPerNode / 2);
                }
            }

            public static ImmutableIdDispenser Empty
            {
                get
                {
                    // The empty dispenser has the id=0 allocated, so it is not really empty.
                    // It saves us from dealing with the corner case of true empty dispenser,
                    // and it ensures that ManagedThreadIdNone will not be ever given out.
                    return new ImmutableIdDispenser(null, null, 1, BitsPerNode, 1);
                }
            }

            public ImmutableIdDispenser AllocateId(out int id)
            {
                if (_used == _size)
                {
                    id = _size;
                    return new ImmutableIdDispenser(this, null, _size + 1, checked(2 * _size + BitsPerNode), 1);
                }

                var bitmap = _bitmap;
                var left = _left;
                var right = _right;

                // Any free bits in current node?
                if (bitmap != UInt32.MaxValue)
                {
                    int bit = 0;
                    while ((bitmap & (uint)(1 << bit)) != 0)
                        bit++;
                    bitmap |= (uint)(1 << bit);
                    id = ChildSize + bit;
                }
                else
                {
                    Debug.Assert(ChildSize > 0);
                    if (left == null)
                    {
                        left = new ImmutableIdDispenser(null, null, 1, ChildSize, 1);
                        id = left.ChildSize;
                    }
                    else
                    if (right == null)
                    {
                        right = new ImmutableIdDispenser(null, null, 1, ChildSize, 1);
                        id = ChildSize + BitsPerNode + right.ChildSize;
                    }
                    else
                    {
                        if (left._used < right._used)
                        {
                            Debug.Assert(left._used < left._size);
                            left = left.AllocateId(out id);
                        }
                        else
                        {
                            Debug.Assert(right._used < right._size);
                            right = right.AllocateId(out id);
                            id += (ChildSize + BitsPerNode);
                        }
                    }
                }
                return new ImmutableIdDispenser(left, right, _used + 1, _size, bitmap);
            }

            public ImmutableIdDispenser RecycleId(int id)
            {
                Debug.Assert(id < _size);

                if (_used == 1)
                    return null;

                var bitmap = _bitmap;
                var left = _left;
                var right = _right;

                int childSize = ChildSize;
                if (id < childSize)
                {
                    left = left.RecycleId(id);
                }
                else
                {
                    id -= childSize;
                    if (id < BitsPerNode)
                    {
                        Debug.Assert((bitmap & (uint)(1 << id)) != 0);
                        bitmap &= ~(uint)(1 << id);
                    }
                    else
                    {
                        right = right.RecycleId(id - BitsPerNode);
                    }
                }
                return new ImmutableIdDispenser(left, right, _used - 1, _size, bitmap);
            }
        }

        [ThreadStatic]
        private static ManagedThreadId t_currentThreadId;
        [ThreadStatic]
        private static int t_currentManagedThreadId;

        // We have to avoid the static constructors on the ManagedThreadId class, otherwise we can run into stack overflow as first time Current property get called, 
        // the runtime will ensure running the static constructor and this process will call the Current property again (when taking any lock) 
        //      System::Environment.get_CurrentManagedThreadId
        //      System::Threading::Lock.Acquire
        //      System::Runtime::CompilerServices::ClassConstructorRunner::Cctor.GetCctor
        //      System::Runtime::CompilerServices::ClassConstructorRunner.EnsureClassConstructorRun
        //      System::Threading::ManagedThreadId.get_Current
        //      System::Environment.get_CurrentManagedThreadId

        private static ImmutableIdDispenser s_idDispenser;

        private int _managedThreadId;

        internal const int ManagedThreadIdNone = 0;

        public static int Current
        {
            get
            {
                int currentManagedThreadId = t_currentManagedThreadId;
                if (currentManagedThreadId == ManagedThreadIdNone)
                    return MakeForCurrentThread();
                else
                    return currentManagedThreadId;
            }
        }

        private static int MakeForCurrentThread()
        {
            if (s_idDispenser == null)
                Interlocked.CompareExchange(ref s_idDispenser, ImmutableIdDispenser.Empty, null);

            int id;

            var priorIdDispenser = Volatile.Read(ref s_idDispenser);
            for (;;)
            {
                var updatedIdDispenser = priorIdDispenser.AllocateId(out id);
                var interlockedResult = Interlocked.CompareExchange(ref s_idDispenser, updatedIdDispenser, priorIdDispenser);
                if (Object.ReferenceEquals(priorIdDispenser, interlockedResult))
                    break;
                priorIdDispenser = interlockedResult; // we already have a volatile read that we can reuse for the next loop
            }

            Debug.Assert(id != ManagedThreadIdNone);

            t_currentThreadId = new ManagedThreadId(id);
            t_currentManagedThreadId = id;

            return id;
        }

        private ManagedThreadId(int managedThreadId)
        {
            _managedThreadId = managedThreadId;
        }

        ~ManagedThreadId()
        {
            if (RuntimeImports.RhHasShutdownStarted())
            {
                return;
            }

            if (_managedThreadId == ManagedThreadIdNone)
            {
                return;
            }

            try
            {
                var priorIdDispenser = Volatile.Read(ref s_idDispenser);
                for (;;)
                {
                    var updatedIdDispenser = s_idDispenser.RecycleId(_managedThreadId);
                    var interlockedResult = Interlocked.CompareExchange(ref s_idDispenser, updatedIdDispenser, priorIdDispenser);
                    if (Object.ReferenceEquals(priorIdDispenser, interlockedResult))
                        break;
                    priorIdDispenser = interlockedResult; // we already have a volatile read that we can reuse for the next loop
                }
            }
            catch (OutOfMemoryException)
            {
                // Try to recycle the id next time
                RuntimeImports.RhReRegisterForFinalize(this);
            }
        }
    }
}
