using System.Runtime.CompilerServices;

namespace System
{
    public class String : Object
    {
        public int Length { get; }

        public unsafe char this[int index]
        {
            get
            {
                fixed (char* chars = &GetPinnableReference())
                {
                    return chars[index];
                }
            }
        }

        public unsafe ref char GetPinnableReference()
        {
            // Object memory layout (if TypeHeader is 4 bytes):
            // +0: TypeHeader
            // +4: Length (since it's the first and only field in String)
            // +8: Characters
            byte* basePtr = (byte*)Unsafe.AsPointer(this);
            return ref *(char*)(basePtr + 8);
        }

        public static bool operator ==(String a, String b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null) return false;
            if (a.Length != b.Length) return false;

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        public static bool operator !=(String a, String b) => !(a == b);

        public override bool Equals(object obj)
        {
            if (obj is String s) return this == s;
            return false;
        }

        public override int GetHashCode() => Length;

        public static unsafe String Concat(String str0, String str1)
        {
            if (ReferenceEquals(str0, null)) return str1;
            if (ReferenceEquals(str1, null)) return str0;

            int len0 = str0.Length;
            int len1 = str1.Length;
            int totalLen = len0 + len1;

            // Allocate Memory: 8 bytes for object headers + totalLen * 2 bytes for chars + 2 bytes for null-terminator
            int allocSize = 8 + (totalLen + 1) * 2;
            int ptr = System.MCU.Memory.Alloc(allocSize);

            uint newStrData = (uint)ptr;
            uint str0Data = (uint)Unsafe.AsPointer(str0);
            uint str1Data = (uint)Unsafe.AsPointer(str1);

            // Copy TypeHeader (first 4 bytes)
            System.MCU.Memory.Write(newStrData, System.MCU.Memory.Read(str0Data));
            // Set new length
            System.MCU.Memory.Write(newStrData + 4, (uint)totalLen);

            // Copy chars
            for (int i = 0; i < len0; i++)
            {
                System.MCU.Memory.Write(newStrData + 8 + (uint)(i * 2), System.MCU.Memory.Read(str0Data + 8 + (uint)(i * 2)));
            }
            for (int i = 0; i < len1; i++)
            {
                System.MCU.Memory.Write(newStrData + 8 + (uint)((len0 + i) * 2), System.MCU.Memory.Read(str1Data + 8 + (uint)(i * 2)));
            }

            return Unsafe.As<String>((void*)ptr);
        }
    }
}
