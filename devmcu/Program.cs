using System;

namespace devmcu
{
    internal class Program
    {
        const int d1 = 33;

        static void Main()
        {
            const int d2 = 11;
            while (true)
            {
                var q = d2 * d1;

                q = q > 99 ? (d2 * q) : (d1 * q);


                var ultraTest = (1 + (2 * (3 + 4))) + (5 * 6);
                var a = 99;

                a = (a + 55) * a;

                a += 98;

                a -= 97;

                a *= 96;

                a /= 95;



                a = a + 94;

                a = a - 93;

                a = a * 92;

                a = a / 91;



                a = a + 94 * a;

                a = a - 93 * a;

                a = a * 92 * a;

                a = a / 91 * a;



                a = a + 94 / a;

                a = a - 93 / a;

                a = a * 92 / a;

                a = a / 91 / a;


                bool b = true;

                if (b)
                    b = false;

                //Console.WriteLine("Hello, World!");
                // Тест 1: Приоритет умножения над сложением (результат должен быть 110, а не 120)
                var test1 = 10 + 20 * 5;

                // Тест 2: Скобки меняют приоритет (результат должен быть 150)
                //var test2 = (10 + 20) * 5;

                while ((10 + 20) * 5 > 99)
                {

                }

                // Тест 3: Глубокая вложенность и разные операторы
                //var test3 = (a + (5 * (int)b)) / (a - 1);

                // Тест 4: "Матрешка" из скобок
                //var test4 = 100 / (10 + (5 * (2 + 1)));

                if (a == 11 || a > 10 || a < 12 || a >= 66 || a <= 99 && a == 31)
                    Test();
                else if (a == 150 || (a < 22 && a > 31) && !b)
                    Test();
                else
                    Test2(a);

                Test();
                //object obj = Main;
            }
        }


        static void Test()
        {


            var a = 22;

            a += 21;

            a -= 10;
            //Console.WriteLine("Test");
        }


        static void Test2(int b)
        {


            var a = 22 + b;

            a += 21 * b;

            a -= 10 * b;
            //Console.WriteLine("Test");
        }
    }
}

