using NETMCUCore.STM;

namespace devmcu
{
    public class Program
    {
        // Для Black Pill F401: LED на PC13
        const int LED_PIN = 13;

        public static void ModifyValues(ref int a, out int b)
        {
            b = a * 2;
            a = a + 5;
        }

        public static void TestExpressions()
        {
            int size = sizeof(int);
            int sizeByte = sizeof(byte);

            var tType = typeof(int);

            int[] arr = new int[5];

            // Default
            int defInt = default(int);
            int[] defArray = default;

            // Unary operators tests
            int counter = 0;
            counter++;
            ++counter;
            counter--;
            --counter;

            bool isDone = false;
            if (!isDone && counter == 1) 
            {
               isDone = true;
            }
        }

        public static void Main()
        {
            int val1 = 10;
            int val2;
            ModifyValues(ref val1, out val2);

            TestExpressions();
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
