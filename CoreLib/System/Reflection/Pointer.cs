// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

////////////////////////////////////////////////////////////////////////////////
//
// This is a wrapper class for Pointers
// 
//
// 
//  
//
namespace System.Reflection {
    using System;
    using CultureInfo = System.Globalization.CultureInfo;
    using System.Security;
    using System.Diagnostics.Contracts;

    [CLSCompliant(false)]
    [Serializable]
    [System.Runtime.InteropServices.ComVisible(true)]
    public sealed class Pointer {
        [SecurityCritical]
        unsafe private void* _ptr;
        private RuntimeType _ptrType;

        private Pointer() {}

        // This method will box an pointer.  We save both the
        //    value and the type so we can access it from the native code
        //    during an Invoke.
        [System.Security.SecurityCritical]  // auto-generated
        public static unsafe Object Box(void *ptr,Type type) {
            if (type == null)
                throw new ArgumentNullException("type");
            if (!type.IsPointer)
                throw new ArgumentException(Environment.GetResourceString("Arg_MustBePointer"),"ptr");
            Contract.EndContractBlock();

            RuntimeType rt = type as RuntimeType;
            if (rt == null)
                throw new ArgumentException(Environment.GetResourceString("Arg_MustBePointer"), "ptr");

            Pointer x = new Pointer();
            x._ptr = ptr;
            x._ptrType = rt;
            return x;
        }

        // Returned the stored pointer.
        [System.Security.SecurityCritical]  // auto-generated
        public static unsafe void* Unbox(Object ptr) {
            if (!(ptr is Pointer))
                throw new ArgumentException(Environment.GetResourceString("Arg_MustBePointer"),"ptr");
            return ((Pointer)ptr)._ptr;
        }
    
        internal RuntimeType GetPointerType() {
            return _ptrType;
        }
    
        [System.Security.SecurityCritical]  // auto-generated
        internal unsafe Object GetPointerValue() {
            return (IntPtr)_ptr;
        }

#if FEATURE_SERIALIZATION
        [System.Security.SecurityCritical]
        unsafe void ISerializable.GetObjectData(SerializationInfo info, StreamingContext context) {
            info.AddValue("_ptr", new IntPtr(_ptr));
            info.AddValue("_ptrType", _ptrType);
        }
#endif
    }
}
