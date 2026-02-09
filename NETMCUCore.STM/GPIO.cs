using System;
using System.MCU;
using System.MCU.Compiler.Attributes;

namespace NETMCUCore.STM
{

    public class HAL_RCC_API
    {
        // Эту функцию нам нужно будет добавить в main.c в Docker
        [NativeCall("System_Enable_GPIO_Clock")]
        public static extern void NativeEnableClock(int portIdx);
    }

    public class HAL_GPIO_API
    {
        [NativeCall("HAL_GPIO_Init")]
        public static extern void NativeInit(uint portAddr, ref GPIO_InitTypeDef config);

        [NativeCall("HAL_GPIO_TogglePin")]
        public static extern void NativeToggle(uint port, uint pin);

        [NativeCall("HAL_GPIO_WritePin")]
        public static extern void NativeWrite(uint port, uint pin, int state);
    }

    public class GPIO
    {
        // Базовый адрес GPIOA на шине AHB1
        private const uint GPIO_BASE = 0x40020000;
        // Регистр включения тактирования портов
        private const uint RCC_AHB1ENR = 0x40023830;

        // Базовые адреса портов для F401/F411
        // Порты идут с шагом 0x400
        private static uint GetPortAddress(GPIO_Port portIdx)
            => GPIO_BASE + ((uint)portIdx * 0x400);

        public static void EnableClock(GPIO_Port port)
        {
            //// Читаем текущее значение, ставим бит порта и пишем обратно
            //uint val = Memory.Read(RCC_AHB1ENR);
            //val |= (uint)(1 << (int)port);
            //Memory.Write(RCC_AHB1ENR, val);
        }
        public static void SetMode(GPIO_Port port, int pin, GPIO_Mode mode, GPIO_Pull pull = GPIO_Pull.NoPull, GPIO_Speed speed = GPIO_Speed.VeryHigh, uint alternate = 0)
        {
            const int abssss = 1;
            uint portAddr = GetPortAddress(port);

            var config = new GPIO_InitTypeDef(); 
            config.Pin = (uint)(1 << pin);
            config.Mode = mode;
            config.Pull = pull;
            config.Speed = speed;
            config.Alternate = alternate;

            // Твой линковщик передаст адрес этой структуры в R1
            HAL_GPIO_API.NativeInit(portAddr, ref config);
        }
        public static void Toggle(GPIO_Port port, int pin)
        {
            uint portAddr = GetPortAddress(port);
            HAL_GPIO_API.NativeToggle(portAddr, (uint)(1 << pin));
        }
    }
}
