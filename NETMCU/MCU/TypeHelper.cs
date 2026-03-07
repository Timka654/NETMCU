using System;

namespace System.MCU
{
    public static unsafe class TypeHelper
    {
        // Layout:
        // 0: Size
        // 4: NamePtr
        // 8: VTableCount
        // 12.. : VTable
        // 12 + VTableCount * 4: InterfaceCount
        // then for each interface:
        //   uint InterfaceTypePtr
        //   uint MethodCount
        //   uint[] Methods
        
        public static IntPtr FindInterfaceMethod(IntPtr typePtr, IntPtr interfacePtr, int methodIndex)
        {
            if (typePtr.ToInt32() == 0) return new IntPtr(0);

            uint* ptr = (uint*)(void*)typePtr;
            uint* interfaceData = ptr + 3 + ptr[2]; // ptr[2] is vtableCount

            uint interfaceCount = *interfaceData;
            interfaceData++;

            for (uint i = 0; i < interfaceCount; i++)
            {
                if (*interfaceData == (uint)interfacePtr.ToInt32())
                {
                    interfaceData++; // move to methodCount
                    if (methodIndex < *interfaceData)
                    {
                        interfaceData++; // move to first method
                        return new IntPtr((void*)interfaceData[methodIndex]);
                    }
                    return new IntPtr(0);
                }

                interfaceData++; // methodCount
                interfaceData += *interfaceData + 1; // skip this interface's whole block (methodCount + elements)
            }

            return new IntPtr(0);
        }
    }
}
