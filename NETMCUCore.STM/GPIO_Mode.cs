namespace NETMCUCore.STM
{
    public enum GPIO_Mode : uint
    {
        Input = 0x00000000,
        OutputPushPull = 0x00000001,
        OutputOpenDrain = 0x00000011,
        AlternativeFunctionPushPull = 0x00000002,
        AlternativeFunctionOpenDrain = 0x00000012,
        Analog = 0x00000003
    }
}
