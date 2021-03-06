// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Reflection;
using System.Collections.Generic;
using Debug = System.Diagnostics.Debug;

namespace Internal.Reflection.Execution
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////
    //
    //                                    **** WARNING ****
    //
    // A large portion of the logic present in this file is duplicated in ndp\rh\src\rtm\system\runtime\typecast.cs
    //
    //                                    **** WARNING ****
    //
    /////////////////////////////////////////////////////////////////////////////////////////////////////

    // This is not a general purpose type comparison facility. It is limited to what constraint validation needs.
    internal static partial class ConstraintValidator
    {
        static bool ImplementsInterface(TypeInfo pObjType, TypeInfo pTargetType)
        {
            Debug.Assert(!pTargetType.IsArray, "did not expect array type");
            Debug.Assert(pTargetType.IsInterface, "IsInstanceOfInterface called with non-interface EEType");

            foreach (var pInterfaceType in pObjType.ImplementedInterfaces)
            {
                if (AreTypesEquivalentInternal(pInterfaceType.GetTypeInfo(), pTargetType))
                {
                    return true;
                }
            }

            // We did not find the interface type in the list of supported interfaces. There's still one
            // chance left: if the target interface is generic and one or more of its type parameters is co or
            // contra variant then the object can still match if it implements a different instantiation of
            // the interface with type compatible generic arguments.
            //
            // An additional edge case occurs because of array covariance. This forces us to treat any generic
            // interfaces implemented by arrays as covariant over their one type parameter.
            // if (pTargetType.HasGenericVariance || (fArrayCovariance && pTargetType.IsGenericType))
            //
            if (pTargetType.IsGenericType)
            {
                bool fArrayCovariance = pObjType.IsArray;
                Type pTargetGenericType = pTargetType.GetGenericTypeDefinition();

                // Fetch the instantiations lazily only once we get a potential match
                Type[] pTargetInstantiation = null;
                Type[] pTargetGenericInstantiation = null;

                foreach (var pInterface in pObjType.ImplementedInterfaces)
                {
                    TypeInfo pInterfaceType = pInterface.GetTypeInfo();

                    // We can ignore interfaces which are not also marked as having generic variance
                    // unless we're dealing with array covariance. 
                    // if (pInterfaceType.HasGenericVariance || (fArrayCovariance && pInterfaceType.IsGenericType))

                    if (!pInterfaceType.IsGenericType)
                        continue;

                    // If the generic types aren't the same then the types aren't compatible.
                    if (!pInterfaceType.GetGenericTypeDefinition().Equals(pTargetGenericType))
                        continue;

                    Type[] pInterfaceInstantiation = pInterfaceType.GenericTypeArguments;

                    if (pTargetInstantiation == null)
                    {
                        pTargetInstantiation = pTargetType.GenericTypeArguments;

                        if (!fArrayCovariance)
                            pTargetGenericInstantiation = pTargetGenericType.GetTypeInfo().GenericTypeParameters;
                    }

                    // Compare the instantiations to see if they're compatible taking variance into account.
                    if (TypeParametersAreCompatible(pInterfaceInstantiation,
                                                    pTargetInstantiation,
                                                    pTargetGenericInstantiation,
                                                    fArrayCovariance))
                        return true;

                    if (fArrayCovariance)
                    {
                        Debug.Assert(pInterfaceInstantiation.Length == 1, "arity mismatch for array generic interface");
                        Debug.Assert(pTargetInstantiation.Length == 1, "arity mismatch for array generic interface");

                        // Special case for generic interfaces on arrays. Arrays of integral types (including enums)
                        // can be cast to generic interfaces over the integral types of the same size. For example
                        // int[] . IList<uint>.
                        if (ArePrimitveTypesEquivalentSize(pInterfaceInstantiation[0].GetTypeInfo(),
                                                           pTargetInstantiation[0].GetTypeInfo()))
                        {
                            // We have checked that the interface type definition matches above. The checks are ordered differently
                            // here compared with rtm\system\runtime\typecast.cs version because of TypeInfo does not let us do
                            // the HasGenericVariance optimization.
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        // Compare two types to see if they are compatible via generic variance.
        static bool TypesAreCompatibleViaGenericVariance(TypeInfo pSourceType, TypeInfo pTargetType)
        {
            Type pTargetGenericType = pTargetType.GetGenericTypeDefinition();
            Type pSourceGenericType = pSourceType.GetGenericTypeDefinition();

            // If the generic types aren't the same then the types aren't compatible.
            if (pTargetGenericType.Equals(pSourceGenericType))
            {
                // Compare the instantiations to see if they're compatible taking variance into account.
                if (TypeParametersAreCompatible(pSourceType.GenericTypeArguments,
                                                pTargetType.GenericTypeArguments,
                                                pTargetGenericType.GetTypeInfo().GenericTypeParameters,
                                                false))
                {
                    return true;
                }
            }

            return false;
        }

        // Compare two sets of generic type parameters to see if they're assignment compatible taking generic
        // variance into account. It's assumed they've already had their type definition matched (which
        // implies their arities are the same as well). The fForceCovariance argument tells the method to
        // override the defined variance of each parameter and instead assume it is covariant. This is used to
        // implement covariant array interfaces.
        static bool TypeParametersAreCompatible(Type[] pSourceInstantiation,
                                                        Type[] pTargetInstantiation,
                                                        Type[] pVarianceInfo,
                                                        bool fForceCovariance)
        {
            // The types represent different instantiations of the same generic type. The
            // arity of both had better be the same.
            Debug.Assert(pSourceInstantiation.Length == pTargetInstantiation.Length, "arity mismatch betweeen generic instantiations");

            Debug.Assert(fForceCovariance || pTargetInstantiation.Length == pVarianceInfo.Length, "arity mismatch betweeen generic instantiations");

            // Walk through the instantiations comparing the cast compatibility of each pair
            // of type args.
            for (int i = 0; i < pTargetInstantiation.Length; i++)
            {
                TypeInfo pTargetArgType = pTargetInstantiation[i].GetTypeInfo();
                TypeInfo pSourceArgType = pSourceInstantiation[i].GetTypeInfo();

                GenericParameterAttributes varType;
                if (fForceCovariance)
                    varType = GenericParameterAttributes.Covariant;
                else
                    varType = pVarianceInfo[i].GetTypeInfo().GenericParameterAttributes & GenericParameterAttributes.VarianceMask;

                switch (varType)
                {
                    case GenericParameterAttributes.None:
                        // Non-variant type params need to be identical.

                        if (!AreTypesEquivalentInternal(pSourceArgType, pTargetArgType))
                            return false;

                        break;

                    case GenericParameterAttributes.Covariant:
                        // For covariance (or out type params in C#) the object must implement an
                        // interface with a more derived type arg than the target interface. Or
                        // the object interface can have a type arg that is an interface
                        // implemented by the target type arg.
                        // For instance:
                        //   class Foo : ICovariant<String> is ICovariant<Object>
                        //   class Foo : ICovariant<Bar> is ICovariant<IBar>
                        //   class Foo : ICovariant<IBar> is ICovariant<Object>

                        if (!AreTypesAssignableInternal(pSourceArgType, pTargetArgType, false, false))
                            return false;

                        break;

                    case GenericParameterAttributes.Contravariant:
                        // For contravariance (or in type params in C#) the object must implement
                        // an interface with a less derived type arg than the target interface. Or
                        // the object interface can have a type arg that is a class implementing
                        // the interface that is the target type arg.
                        // For instance:
                        //   class Foo : IContravariant<Object> is IContravariant<String>
                        //   class Foo : IContravariant<IBar> is IContravariant<Bar>
                        //   class Foo : IContravariant<Object> is IContravariant<IBar>

                        if (!AreTypesAssignableInternal(pTargetArgType, pSourceArgType, false, false))
                            return false;

                        break;

                    default:
                        Debug.Assert(false, "unknown generic variance type");
                        return false;
                }
            }

            return true;
        }

        //
        // Determines if a value of the source type can be assigned to a location of the target type.
        // It does not handle ICastable, and cannot since we do not have an actual object instance here.
        // This routine assumes that the source type is boxed, i.e. a value type source is presumed to be
        // compatible with Object and ValueType and an enum source is additionally compatible with Enum.
        //
        static bool AreTypesAssignable(TypeInfo pSourceType, TypeInfo pTargetType)
        {
            // Special case: T can be cast to Nullable<T> (where T is a value type). Call this case out here
            // since this is only applicable if T is boxed, which is not true for any other callers of
            // AreTypesAssignableInternal, so no sense making all the other paths pay the cost of the check.
            if (pTargetType.IsNullable() && pSourceType.IsValueType && !pSourceType.IsNullable())
            {
                Type pNullableType = pTargetType.GetNullableType();

                return AreTypesEquivalentInternal(pSourceType, pNullableType.GetTypeInfo());
            }

            return AreTypesAssignableInternal(pSourceType, pTargetType, true, false);
        }

        // Internally callable version of the export method above. Has two additional parameters:
        //  fBoxedSource            : assume the source type is boxed so that value types and enums are
        //                            compatible with Object, ValueType and Enum (if applicable)
        //  fAllowSizeEquivalence   : allow identically sized integral types and enums to be considered
        //                            equivalent (currently used only for array element types)
        static bool AreTypesAssignableInternal(TypeInfo pSourceType, TypeInfo pTargetType, bool fBoxedSource, bool fAllowSizeEquivalence)
        {
            //
            // Are the types identical?
            //
            if (AreTypesEquivalentInternal(pSourceType, pTargetType))
                return true;

            //
            // Handle cast to interface cases.
            //
            if (pTargetType.IsInterface)
            {
                // Value types can only be cast to interfaces if they're boxed.
                if (!fBoxedSource && pSourceType.IsValueType)
                    return false;

                if (ImplementsInterface(pSourceType, pTargetType))
                    return true;

                // Are the types compatible due to generic variance?
                // if (pTargetType.HasGenericVariance && pSourceType.HasGenericVariance)
                if (pTargetType.IsGenericType && pSourceType.IsGenericType)
                    return TypesAreCompatibleViaGenericVariance(pSourceType, pTargetType);

                return false;
            }
            if (pSourceType.IsInterface)
            {
                // The only non-interface type an interface can be cast to is Object.
                return pTargetType.IsSystemObject();
            }

            //
            // Handle cast to array cases.
            //
            if (pTargetType.IsArray)
            {
                if (pSourceType.IsArray)
                {
                    if (pSourceType.GetElementType().GetTypeInfo().IsPointer)
                    {
                        // If the element types are pointers, then only exact matches are correct.
                        // As we've already called AreTypesEquivalent at the start of this function,
                        // return false as the exact match case has already been handled.
                        // int** is not compatible with uint**, nor is int*[] oompatible with uint*[].
                        return false;
                    }
                    else
                    {
                        // Source type is also a pointer. Are the element types compatible? Note that using
                        // AreTypesAssignableInternal here handles array covariance as well as IFoo[] . Foo[]
                        // etc. Pass false for fBoxedSource since int[] is not assignable to object[].
                        return AreTypesAssignableInternal(pSourceType.GetElementType().GetTypeInfo(), pTargetType.GetElementType().GetTypeInfo(), false, true);
                    }
                }

                // Can't cast a non-array type to an array.
                return false;
            }
            if (pSourceType.IsArray)
            {
                // Target type is not an array. But we can still cast arrays to Object or System.Array.
                return pTargetType.IsSystemObject() || pTargetType.IsSystemArray();
            }

            //
            // Handle pointer cases
            //
            if (pTargetType.IsPointer)
            {
                if (pSourceType.IsPointer)
                {
                    if (pSourceType.GetElementType().GetTypeInfo().IsPointer)
                    {
                        // If the element types are pointers, then only exact matches are correct.
                        // As we've already called AreTypesEquivalent at the start of this function,
                        // return false as the exact match case has already been handled.
                        // int** is not compatible with uint**, nor is int*[] compatible with uint*[].
                        return false;
                    }
                    else
                    {
                        // Source type is also a pointer. Are the element types compatible? Note that using
                        // AreTypesAssignableInternal here handles array covariance as well as IFoo[] . Foo[]
                        // etc. Pass false for fBoxedSource since int[] is not assignable to object[].
                        return AreTypesAssignableInternal(pSourceType.GetElementType().GetTypeInfo(), pTargetType.GetElementType().GetTypeInfo(), false, true);
                    }
                }

                return false;
            }
            else if (pSourceType.IsPointer)
            {
                return false;
            }

            //
            // Handle cast to other (non-interface, non-array) cases.
            //

            if (pSourceType.IsValueType)
            {
                // Certain value types of the same size are treated as equivalent when the comparison is
                // between array element types (indicated by fAllowSizeEquivalence). These are integer types
                // of the same size (e.g. int and uint) and the base type of enums vs all integer types of the
                // same size.
                if (fAllowSizeEquivalence && pTargetType.IsValueType)
                {
                    if (ArePrimitveTypesEquivalentSize(pSourceType, pTargetType))
                        return true;

                    // Non-identical value types aren't equivalent in any other case (since value types are
                    // sealed).
                    return false;
                }

                // If the source type is a value type but it's not boxed then we've run out of options: the types
                // are not identical, the target type isn't an interface and we're not allowed to check whether
                // the target type is a parent of this one since value types are sealed and thus the only matches
                // would be against Object, ValueType or Enum, all of which are reference types and not compatible
                // with non-boxed value types.
                if (!fBoxedSource)
                    return false;
            }

            //
            // Are the types compatible via generic variance?
            //
            // if (pTargetType.HasGenericVariance && pSourceType.HasGenericVariance)
            if (pTargetType.IsGenericType && pSourceType.IsGenericType)
            {
                if (TypesAreCompatibleViaGenericVariance(pSourceType, pTargetType))
                    return true;
            }

            // Is the source type derived from the target type?
            if (IsDerived(pSourceType, pTargetType))
                return true;

            return false;
        }

        static bool IsDerived(TypeInfo pDerivedType, TypeInfo pBaseType)
        {
            Debug.Assert(!pBaseType.IsInterface, "did not expect interface type");

            for (; ;)
            {
                if (AreTypesEquivalentInternal(pDerivedType, pBaseType))
                    return true;

                Type baseType = pDerivedType.BaseType;
                if (baseType == null)
                    return false;

                pDerivedType = baseType.GetTypeInfo();
            }
        }

        // Method to compare two types pointers for type equality
        // We cannot just compare the pointers as there can be duplicate type instances
        // for cloned and constructed types.
        static bool AreTypesEquivalentInternal(TypeInfo pType1, TypeInfo pType2)
        {
            if (!pType1.IsInstantiatedTypeInfo() && !pType2.IsInstantiatedTypeInfo())
                return pType1.Equals(pType2);

            if (pType1.IsGenericType && pType2.IsGenericType)
            {
                if (!pType1.GetGenericTypeDefinition().Equals(pType2.GetGenericTypeDefinition()))
                    return false;

                Type[] args1 = pType1.GenericTypeArguments;
                Type[] args2 = pType2.GenericTypeArguments;
                Debug.Assert(args1.Length == args2.Length);

                for (int i = 0; i < args1.Length; i++)
                {
                    if (!AreTypesEquivalentInternal(args1[i].GetTypeInfo(), args2[i].GetTypeInfo()))
                        return false;
                }

                return true;
            }

            if (pType1.IsArray && pType2.IsArray)
            {
                if (pType1.GetArrayRank() != pType2.GetArrayRank())
                    return false;

                return AreTypesEquivalentInternal(pType1.GetElementType().GetTypeInfo(), pType2.GetElementType().GetTypeInfo());
            }

            if (pType1.IsPointer && pType2.IsPointer)
            {
                return AreTypesEquivalentInternal(pType1.GetElementType().GetTypeInfo(), pType2.GetElementType().GetTypeInfo());
            }

            return false;
        }

        static bool ArePrimitveTypesEquivalentSize(TypeInfo pType1, TypeInfo pType2)
        {
            int normalizedType1 = NormalizedPrimitiveTypeSizeForIntegerTypes(pType1);
            if (normalizedType1 == 0)
                return false;

            int normalizedType2 = NormalizedPrimitiveTypeSizeForIntegerTypes(pType2);

            return normalizedType1 == normalizedType2;
        }
    }
}
