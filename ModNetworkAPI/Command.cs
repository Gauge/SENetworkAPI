namespace ModNetworkAPI
{
    internal class Command
    {
        public ulong SteamId { get; set; }

        public string CommandString { get; set; }

        public string Message { get; set; }

        public object Data { get; set; }
    }
}
