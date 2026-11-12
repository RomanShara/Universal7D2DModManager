namespace GameCode
{
    public class Block
    {
        public string Name { get; private set; }
        public int Stability { get; set; }

        public static Block GetBlock(string name) => new Block { Name = name };

        public void SetName(string name)
        {
            Name = name;
        }

        // "Game v2" renamed the API: LegacyCall is gone, NewCall appeared.
        public void NewCall()
        {
        }
    }

    public class ItemClass
    {
        public string Name { get; set; }

        public static ItemClass GetItem(string name) => new ItemClass { Name = name };
    }
}
