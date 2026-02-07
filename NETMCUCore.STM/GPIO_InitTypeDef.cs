namespace NETMCUCore.STM
{
    //[StructLayout(LayoutKind.Sequential)]
    public struct GPIO_InitTypeDef
    {
        public uint Pin;       // Маска пина (1 << 13)
        public GPIO_Mode Mode;      // Режим (Output, Input...)
        public GPIO_Pull Pull;      // Подтяжка (None, Up, Down)
        public GPIO_Speed Speed;     // Скорость шины
        public uint Alternate; // Альтернативная функция (для UART/SPI)
    }
}
