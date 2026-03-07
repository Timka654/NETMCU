namespace System.Text
{
    public class StringBuilder
    {
        public StringBuilder() { }

        public StringBuilder Append(string value) => this;
        public StringBuilder Append(object value) => this;
        public StringBuilder Append(int value) => this;
        public StringBuilder Append(bool value) => this;
        public StringBuilder Append(char value) => this;
        public StringBuilder Append(short value) => this;
        public StringBuilder Append(long value) => this;
        public StringBuilder Append(byte value) => this;

        public override string ToString()
        {
            return "StringBuilder";
        }
    }
}