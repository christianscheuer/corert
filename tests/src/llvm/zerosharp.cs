using System;
using System.Runtime.InteropServices;

#region A couple very basic things
namespace System
{
    public class Object { IntPtr m_pEEType; }
    public struct Void { }
    public struct Boolean { }
    public struct Char { }
    public struct SByte { }
    public struct Byte { }
    public struct Int16 { }
    public struct UInt16 { }
    public struct Int32 { }
    public struct UInt32 { }
    public struct Int64 { }
    public struct UInt64 { }
    public struct IntPtr { }
    public struct UIntPtr { }
    public struct Single { }
    public struct Double { }
    public abstract class ValueType { }
    public abstract class Enum : ValueType { }
    public struct Nullable<T> where T : struct { }

    public sealed class String { public readonly int Length; }
    public abstract class Array { }
    public abstract class Delegate { }
    public abstract class MulticastDelegate : Delegate { }

    public struct RuntimeTypeHandle { }
    public struct RuntimeMethodHandle { }
    public struct RuntimeFieldHandle { }

    public class Attribute { }

    namespace Runtime.CompilerServices
    {
        public class RuntimeHelpers
        {
            public static unsafe int OffsetToStringData => sizeof(IntPtr) + sizeof(int);
        }
    }
}
namespace System.Runtime.InteropServices
{
    public sealed class DllImportAttribute : Attribute
    {
        public DllImportAttribute(string dllName) { }
    }
}
#endregion

#region Things needed by ILC
namespace System
{
    namespace Runtime
    {
        internal sealed class RuntimeExportAttribute : Attribute
        {
            public RuntimeExportAttribute(string entry) { }
        }
    }

    class Array<T> : Array { }
}
/* 
unsafe struct ReversePInvokeFrame
{
    public byte* i1;
    public byte* i2;
} */


namespace Internal.Runtime.CompilerHelpers
{
    using System.Runtime;

    class StartupCodeHelpers
    {

        [RuntimeExport("RhpReversePInvoke2")]
        static void RhpReversePInvoke2() { }
        [RuntimeExport("RhpReversePInvokeReturn2")]
        static void RhpReversePInvokeReturn2() { }
        [RuntimeExport("__fail_fast")]
        static void FailFast() { while (true) ; }
        [RuntimeExport("RhpPInvoke")]
        static void RphPinvoke() { }
        [RuntimeExport("RhpPInvokeReturn")]
        static void RphPinvokeReturn() { }
    }
}
#endregion

unsafe class Program
{

    [DllImport("*")]
    private static unsafe extern int printf(int* str, byte* unused);

    private static int Main()
    {
        int hel = 0x006c6548; //"Hel\0"
        printf(&hel, null);

        int lo_ = 0x00206f6c; //"lo \0"
        printf(&lo_, null);

        int wor = 0x00726f57; //"Wor\0"
        printf(&wor, null);

        int ld_ = 0x000a646c; //"ld\n\0"
        printf(&ld_, null);


        return 42;
    }
}
