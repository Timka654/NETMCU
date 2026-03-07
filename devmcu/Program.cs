using System;
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
            TestStrings();
            TestRecords();
            TestGC();
            TestTuples();
            TestGenerics();
        }

        public class Box<T>
        {
            public T Value;
            public Box(T value) { Value = value; }
            public T GetValue() => Value;
        }

        public static void TestGenerics()
        {
            Box<int> intBox = new Box<int>(123);
            int v = intBox.GetValue();

            Box<string> strBox = new Box<string>("Hello Generic!");
            string s = strBox.GetValue();
        }

        public record Point(int X, int Y);

        public static void TestGC()
        {
            GC.Collect();
            long mem = GC.GetTotalMemory(true);
        }

        public static void TestTuples()
        {
            var t = (1, 2);
            int a = t.Item1;
            int b = t.Item2;
            int c = a + b;
        }

        public static void TestRecords()
        {
            Point p1 = new Point(10, 20);
            Point p2 = new Point(10, 20);
            Point p3 = p1 with { Y = 30 };
            bool isEq = p1 == p2;
        }

        public static void TestStrings()
        {
            string hello = "Hello, MCU!";
            int length = hello.Length; // 11
            char h = hello[0]; // 'H'
            char m = hello[7]; // 'M'

            bool isSame = hello == "Hello, MCU!"; // true
            bool isDiff = hello == "Hello"; // false

            string concat = hello + " Test"; // calls String.Concat
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

            int[][] nested = new int[2][];
            nested[0] = new int[] { 1, 2 };
            nested[1] = new int[] { 3, 4, 5 };
            int n = nested[1][2];
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

            // Setup USART1 on PB6(TX) and PB7(RX)
            GPIO.EnableClock(GPIO_Port.PortB);

            var usartConfig = new GPIO_InitTypeDef();
            usartConfig.Pin = (uint)(1 << 6) | (uint)(1 << 7);
            usartConfig.Mode = GPIO_Mode.AlternativeFunctionPushPull;
            usartConfig.Pull = GPIO_Pull.NoPull;
            usartConfig.Speed = GPIO_Speed.VeryHigh;
            usartConfig.Alternate = 7;
            HAL_GPIO_API.NativeInit(0x40020400, ref usartConfig); // 0x40020400 mapping for GPIOB

            USART.Init(USART_Port.USART1, 115200);

            USART.WriteLine(USART_Port.USART1, "Hello, UART!");

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

            ABC.Process<int>(42);
            ABC.Process<string>("test generics");

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

        public static T Process<T>(T value)
        {
            return value;
        }
    }
}
