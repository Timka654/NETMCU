namespace System
{
    public readonly struct IntPtr
    {
        private readonly int _value;
        public IntPtr(int value)
        {
            _value = value;
        }
        public unsafe IntPtr(void* value)
        {
            _value = (int)value;
        }

        public unsafe void* ToPointer() => (void*)_value;
        public int ToInt32() => _value;

        public static implicit operator IntPtr(int value) => new IntPtr(value);
        public static implicit operator int(IntPtr ptr) => ptr._value;
        
        unsafe public static explicit operator IntPtr(void* value) => new IntPtr(value);
        unsafe public static explicit operator void*(IntPtr ptr) => ptr.ToPointer();
    }
}
