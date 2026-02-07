namespace System.MCU.Compiler
{
    public class InputConfigurationErrorMessage(string name, string message)
    {
        public const string InvalidValue = "INVALID_VALUE";
        public const string InvalidType = "INVALID_TYPE";
        public const string RequiredValue = "REQUIRED_VALUE";

        public string Name { get; } = name;
        public string Message { get; } = message;
    }
}
