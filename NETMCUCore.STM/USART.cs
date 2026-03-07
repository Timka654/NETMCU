using System;
using System.MCU;

namespace NETMCUCore.STM
{
    public enum USART_Port : int
    {
        USART1 = 0x40013800,
        USART2 = 0x40004400,
        USART6 = 0x40011400
    }

    public static class USART
    {
        /// <summary>
        /// Enable clock for USART peripheral
        /// </summary>
        public static void EnableClock(USART_Port port)
        {
            // Simplified clock enablement for testing
            uint rcc_apb1enr = 0x40023840;
            uint rcc_apb2enr = 0x40023844;

            if (port == USART_Port.USART2)
            {
                var val = Memory.Read(rcc_apb1enr);
                val |= (1u << 17); // USART2 EN
                Memory.Write(rcc_apb1enr, val);
            }
            else if (port == USART_Port.USART1)
            {
                var val = Memory.Read(rcc_apb2enr);
                val |= (1u << 4); // USART1 EN
                Memory.Write(rcc_apb2enr, val);
            }
            else if (port == USART_Port.USART6)
            {
                var val = Memory.Read(rcc_apb2enr);
                val |= (1u << 5); // USART6 EN
                Memory.Write(rcc_apb2enr, val);
            }
        }

        public static void Init(USART_Port port, uint baudRate)
        {
            EnableClock(port);

            // Assuming system clock is 16MHz (default HSI without specific PLL)
            // BRR = Fclk / BaudRate
            uint sysFreq = 16000000;
            uint usartdiv = sysFreq / baudRate;

            uint baseAddr = (uint)port;
            
            // Disable USART during config
            Memory.Write(baseAddr + 0x0C, 0u); // CR1 = 0
            
            // Set baud rate
            Memory.Write(baseAddr + 0x08, usartdiv); // BRR

            // Enable USART, TX, RX (UE=1, TE=1, RE=1 -> 1<<13 | 1<<3 | 1<<2)
            Memory.Write(baseAddr + 0x0C, (1u << 13) | (1u << 3) | (1u << 2)); // CR1
        }

        public static void WriteChar(USART_Port port, char c)
        {
            uint baseAddr = (uint)port;
            // Wait for TXE (Transmit data register empty)
            while ((Memory.Read(baseAddr + 0x00) & (1u << 7)) == 0)
            {
                // loop
            }
            Memory.Write(baseAddr + 0x04, (uint)c); // DR
        }

        public static void WriteLine(USART_Port port, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                WriteChar(port, text[i]);
            }
            WriteChar(port, '\r');
            WriteChar(port, '\n');
        }
    }
}
