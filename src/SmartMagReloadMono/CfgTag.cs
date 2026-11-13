namespace SmartMagReloadMono
{
    /// <summary>
    /// 给 BepInEx ConfigurationManager 用的显示名覆盖标签。
    /// 类型名必须是 ConfigurationManagerAttributes，它靠反射识别。
    /// </summary>
    public sealed class ConfigurationManagerAttributes
    {
        public string DispName;
        public int? Order;
        public bool? IsAdvanced;
    }
}
