using System;
using System.Collections.Generic;
using Playnite.SDK.Data;

namespace SuccessStory.Models
{
    public class RarityCache
    {
        [SerializationPropertyName("gameName")]
        public string GameName { get; set; }

        [SerializationPropertyName("lastUpdated")]
        public DateTime LastUpdated { get; set; }

        [SerializationPropertyName("trophyRarities")]
        public Dictionary<string, float> TrophyRarities { get; set; } = new Dictionary<string, float>();
    }
}