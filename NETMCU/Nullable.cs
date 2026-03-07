namespace System
{
    public struct Nullable<T> where T : struct
    {
        public bool HasValue { get; }
        internal T value;

        public T Value
        {
            get
            {
                if (!HasValue)
                {
                    // Throw exception (not implemented fully)
                    // throw new InvalidOperationException("Nullable object must have a value.");
                }
                return value;
            }
        }

        public Nullable(T value)
        {
            this.value = value;
            this.HasValue = true;
        }

        public T GetValueOrDefault() => value;
        public T GetValueOrDefault(T defaultValue) => HasValue ? value : defaultValue;

        public static explicit operator T(Nullable<T> value) => value.Value;
        public static implicit operator Nullable<T>(T value) => new Nullable<T>(value);
    }
}
