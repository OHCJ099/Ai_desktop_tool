using System;
using System.Runtime.Serialization;

namespace WindowsClient
{
    [DataContract]
    public class AppConfig
    {
        [DataMember]
        public string ApiUrl { get; set; }

        [DataMember]
        public string ApiKey { get; set; }

        [DataMember]
        public string ModelName { get; set; }

        [DataMember]
        public string SystemPrompt { get; set; }

        [DataMember]
        public double TextOpacity { get; set; }

        [DataMember]
        public double DotOpacity { get; set; }

        [DataMember]
        public string FontWeight { get; set; }

        [DataMember]
        public int TypeDelayMs { get; set; }

        [DataMember]
        public double AutoDelaySeconds { get; set; }

        [DataMember]
        public bool EnableAntiCapture { get; set; }

        [DataMember]
        public bool UseOnlineOcr { get; set; }

        [DataMember]
        public string OnlineOcrModel { get; set; }

        public AppConfig()
        {
            ApiUrl = "https://api.deepseek.com/v1";
            ApiKey = "";
            ModelName = "deepseek-flash";
            SystemPrompt = "请用最简短的语言输出结果。";
            TextOpacity = 100.0;
            DotOpacity = 100.0;
            FontWeight = "Normal";
            TypeDelayMs = 10;
            AutoDelaySeconds = 2.0;
            EnableAntiCapture = true;
            UseOnlineOcr = false;
            OnlineOcrModel = "";
        }
    }
}
