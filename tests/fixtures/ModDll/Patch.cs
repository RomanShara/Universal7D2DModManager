namespace TestMod
{
    /// <summary>
    /// Compiled against game API v1. When analyzed against v2 it must report
    /// a missing member (Block.LegacyCall).
    /// </summary>
    public static class Patch
    {
        public static void Apply()
        {
            var block = GameCode.Block.GetBlock("stone");
            block.SetName("stone2");
            block.LegacyCall();
            var stability = block.Stability;
            var item = GameCode.ItemClass.GetItem("gun");
            item.Name = stability.ToString();
        }
    }
}
