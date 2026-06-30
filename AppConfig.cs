using System;
using System.Runtime.Serialization;

namespace AI_desktop_tool
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
        public string FontWeight { get; set; }

        [DataMember]
        public int TypeDelayMs { get; set; }

        [DataMember]
        public bool EnableAntiCapture { get; set; }

        [DataMember]
        public bool UseOnlineOcr { get; set; }

        [DataMember]
        public string OnlineOcrModel { get; set; }

        public AppConfig()
        {
            ApiUrl = "https://api.siliconflow.cn/v1";
            ApiKey = "sk-qekjfdkiwgbiukcabcqethyukeoukdbieugozkbniuefjmzd";
            ModelName = "deepseek-ai/DeepSeek-V4-Flash";
            SystemPrompt = "你是一个实用的 AI 助手，请用最简短的语言给出答案。";
            TextOpacity = 100.0;
            FontWeight = "Normal";
            TypeDelayMs = 10;
            EnableAntiCapture = false; // Disabled by default
            UseOnlineOcr = false;
            OnlineOcrModel = "PaddlePaddle/PaddleOCR-VL-1.5";
        }
    }
}
