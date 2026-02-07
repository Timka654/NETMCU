using System;

namespace devlib
{
    public class TA(string[] args) : Attribute
    {

    }

    public class Class1
    {
        [TA(new[] { "arg1", "arg2" })]
        public void Method1()
        {
            Console.WriteLine("Hello, World!");
        }
    }
}
