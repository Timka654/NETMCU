using NETMCUCore.STM;

namespace devmcu
{
    interface ITestDevice
    {
        int Ping(int val);
    }

    class BaseClass : ITestDevice
    {
        public virtual int GetValue() { return 10; }
        public int Ping(int val) { return val + 1; }
    }

    class DerivedClass : BaseClass
    {
        public override int GetValue() { return 42; }
    }

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

        public static void RunTests()
        {
            TestDelegates();
            TestVirtualsAndInterfaces();
            TestArrays();
            TestBoxingAndCasting();
        }

        public static void TestDelegates()
        {
            DelegateTest.Test();
        }

        public static void TestVirtualsAndInterfaces()
        {
            BaseClass b1 = new BaseClass();
            BaseClass b2 = new DerivedClass();

            int v1 = b1.GetValue(); // 10
            int v2 = b2.GetValue(); // 42

            ITestDevice device = b2;
            int interfaceR = device.Ping(100); // 101
        }

        public static void TestArrays()
        {
            int[] data = new int[] { 10, 42, 3, 4, 5, 101 };
            int result = ProcessArray(data); 
        }

        public static void TestBoxingAndCasting()
        {
            int result = 165;
            object boxedResult = result; 
            int unboxedResult = (int)boxedResult; 

            bool isInt = boxedResult is int; 
            BaseClass b2 = new DerivedClass();
            BaseClass b3 = b2 as DerivedClass; 

            short narrowed = (short)unboxedResult;
        }

        public static void Main()
        {
            RunTests();

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
