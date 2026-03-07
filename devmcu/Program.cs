using NETMCUCore.STM;

namespace devmcu
{
    public class Program
    {
        // Для Black Pill F401: LED на PC13
        const int LED_PIN = 13;

        public static int ProcessArray(int[] input)
        {
            int sum = 0;
            for (int i = 0; i < input.Length; i++)
            {
                input[i] = input[i] * 2;
            }

            foreach (var item in input)
            {
                sum += item;
            }
            return sum;
        }

        public static void Main()
        {
            int[] data = new int[] { 1, 2, 3, 4, 5 };
            int result = ProcessArray(data);

            HAL.Init();

            WriteLine("Hello, NETMCU!");
            WriteLine("This is a test of string literals.");

            var t = GPIO_Port.PortC;

            if (t == GPIO_Port.PortC)
            {
                GPIO.SetMode(GPIO_Port.PortC, LED_PIN, GPIO_Mode.OutputPushPull);

            }

            // 1. Включаем тактирование порта C
            GPIO.EnableClock(GPIO_Port.PortC);

            // 2. Настраиваем PC13 на выход
            GPIO.SetMode(GPIO_Port.PortC, LED_PIN, GPIO_Mode.OutputPushPull);

            try
            {

            }
            catch (System.Exception)
            {

                throw;
            }

            while (true)
            {
                // 3. Инвертируем состояние
                GPIO.Toggle(GPIO_Port.PortC, LED_PIN);

                // 4. Ждем 500мс (HAL_Delay теперь должен работать)
                HAL.Delay(500);
            }
        }

        static void WriteLine(string t)
        {
            return;
        }
    }

    public class ABC
    {
        public static void TestMethod()
        { 
        
        }
    }
}
