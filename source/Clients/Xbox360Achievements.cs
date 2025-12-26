using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Data;
using CommonPluginsShared;
using SuccessStory.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CommonPluginsShared.Extensions;
using CommonPlayniteShared.Common;
using SteamKit2.GC.TF2.Internal;
using CommonPluginsShared.Models;
using System.Threading.Tasks;
using System.Windows;
using System.Buffers.Binary;

namespace SuccessStory.Clients
{
    struct XdbfHeader
    {
        public UInt32 magic;
        public UInt32 version;
        public UInt32 entry_count;
        public UInt32 entry_used;
        public UInt32 free_count;
        public UInt32 free_used;
    }
    struct XdbfEntry
    {
        public UInt16 section;
        public UInt64 id;
        public UInt32 offset;
        public UInt32 size;

        public byte[] data;
    }

    public class Xbox360Achievements : GenericAchievements
    {
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private string _xeniaProfilePath;
        private bool _isInitialized;

        private Dictionary<string, List<string>> xboxTitleIDs = new Dictionary<string, List<string>>();

        // Path constants
        private const string SUCCESS_STORY_GUID = "cebe6d32-8c46-4459-b993-5a5189d60788";
        private readonly string _playniteAppData;

        // The two main paths we'll use
        private string _successStoryDataDir;
        private string _successStoryXeniaDir;

        public Xbox360Achievements() : base("Xbox360")
        {
            _playniteApi = API.Instance;
            _logger = LogManager.GetLogger();
            _isInitialized = false;
            _xeniaProfilePath = PluginDatabase.PluginSettings.Settings.XeniaProfileFolder;
            _playniteAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            InitializePaths();
            LoadTitleIDs();
            _logger.Info($"Xbox360Achievements initialized with Xenia path: {_xeniaProfilePath}");
        }

        public Xbox360Achievements(IPlayniteAPI playniteApi, string xeniaProfilePath) : base("Xbox360")
        {
            _playniteApi = playniteApi;
            _xeniaProfilePath = xeniaProfilePath;
            _logger = LogManager.GetLogger();
            _isInitialized = false;
            _playniteAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            InitializePaths();
            LoadTitleIDs();
            _logger.Info($"Xbox360Achievements initialized with Xenia path: {_xeniaProfilePath}");
        }

        public override GameAchievements GetAchievements(Game game)
        {
            _logger.Info($"Xbox360: Getting achievements for game: {game.Name}");
            GameAchievements gameAchievements = SuccessStory.PluginDatabase.GetDefault(game);

            if (!EnabledInSettings())
            {
                _logger.Info("Xbox360: Achievement tracking is disabled in settings");
                return gameAchievements;
            }

            //HACK: Added to stop the need to restart playnite for setting change to apply
            _xeniaProfilePath = PluginDatabase.PluginSettings.Settings.XeniaProfileFolder;

            if (!_isInitialized && !string.IsNullOrEmpty(_xeniaProfilePath))
            {
                InitializePaths();
                _isInitialized = true;
            }

            string TitleID = FindAndVerifyTitleID(game.Name);

            if (TitleID != null)
            {
                try
                {
                    if (IsConfigured())
                    {
                        string gameId = game.Id.ToString();
                        if (!string.IsNullOrEmpty(gameId))
                        {
                            string successStoryJsonFile = Path.Combine(_successStoryDataDir, $"{gameId}.json");
                            gameAchievements.Items = ProcessAchievementsImproved(TitleID, successStoryJsonFile, game);

                            if (API.Instance.MainView.SelectedGames?.FirstOrDefault()?.Id == game.Id)
                            {
                                API.Instance.MainView.SelectGames(new List<Guid> { game.Id });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Xbox360: Error processing achievements: {ex.Message}");
                    Common.LogError(ex, false, true, PluginDatabase.PluginName);
                }
            }
            else if (!IsConfigured())
            {
                ShowNotificationPluginNoConfiguration();
            }

            PluginDatabase.AddOrUpdate(gameAchievements);
            gameAchievements.SetRaretyIndicator();

            return gameAchievements;
        }
        private List<Achievement> ProcessAchievementsImproved(string TitleID, string successStoryJsonFile, Game game)
        {
            var achievements = new List<Achievement>();

            try
            {
                achievements = LoadGPD($"{_xeniaProfilePath}{TitleID}.gpd", TitleID);
                SaveAchievementsAtomically(achievements, successStoryJsonFile, game);
            }
            catch (Exception ex)
            {
                _logger.Error($"Xbox360: Failed to process achievements: {ex.Message}");
                Common.LogError(ex, false, true, PluginDatabase.PluginName);
            }

            return achievements;
        }

        private void SaveAchievementsAtomically(List<Achievement> achievements, string finalPath, Game game)
        {
            string gameId = game.Id.ToString();
            string tempPath = $"{_successStoryXeniaDir}\\{gameId}.temp.json";

            try
            {
                GameAchievements gameAchievements;
                if (File.Exists(finalPath))
                {
                    string existingJson = File.ReadAllText(finalPath);
                    gameAchievements = Serialization.FromJson<GameAchievements>(existingJson);

                    foreach (var newAchievement in achievements.Where(a => a.DateUnlocked.HasValue))
                    {
                        var existingAchievement = gameAchievements.Items.FirstOrDefault(x => x.Name == newAchievement.Name);
                        if (existingAchievement != null)
                        {
                            DateTime formattedDate = newAchievement.DateUnlocked.Value;
                            existingAchievement.DateUnlocked = DateTime.ParseExact(
                                formattedDate.ToString("yyyy-MM-ddTHH:mm:ss"),
                                "yyyy-MM-ddTHH:mm:ss",
                                System.Globalization.CultureInfo.InvariantCulture
                            );
                        }
                    }
                }
                else
                {
                    gameAchievements = SuccessStory.PluginDatabase.GetDefault(game);
                    gameAchievements.Items = achievements;
                }

                gameAchievements.DateLastRefresh = DateTime.UtcNow;
                gameAchievements.Name = game.Name;

                string json = Serialization.ToJson(gameAchievements);
                File.WriteAllText(tempPath, json);

                string verificationJson = File.ReadAllText(tempPath);
                var verificationAchievements = Serialization.FromJson<GameAchievements>(verificationJson);

                if (verificationAchievements != null)
                {
                    if (File.Exists(finalPath))
                    {
                        File.Delete(finalPath);
                    }
                    File.Copy(tempPath, finalPath);
                    gameAchievements.IsManual = true;
                    SuccessStory.PluginDatabase.AddOrUpdate(gameAchievements);
                    SuccessStory.PluginDatabase.SetThemesResources(game);
                }
                else
                {
                    throw new Exception("Achievement data verification failed");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Xbox360: Failed to save achievements: {ex.Message}");
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
                throw;
            }
        }

        #region Config
        public void InitializePaths()
        {
            if (string.IsNullOrEmpty(_xeniaProfilePath))
            {
                _logger.Error("Xbox360: Xenia profile path is null or empty");
                return;
            }

            _successStoryDataDir = Directory.Exists(Path.Combine(_playniteAppData, "Playnite", "ExtensionsData", SUCCESS_STORY_GUID, "SuccessStory"))
                ? Path.Combine(_playniteAppData, "Playnite", "ExtensionsData", SUCCESS_STORY_GUID, "SuccessStory".TrimEnd('\\') + '\\')
                : Path.Combine(_playniteApi.Paths.ApplicationPath, "ExtensionsData", SUCCESS_STORY_GUID, "SuccessStory".TrimEnd('\\') + '\\');

            _successStoryXeniaDir = Directory.Exists(Path.Combine(_playniteAppData, "Playnite", "ExtensionsData", SUCCESS_STORY_GUID, "Xenia"))
                ? Path.Combine(_playniteAppData, "Playnite", "ExtensionsData", SUCCESS_STORY_GUID, "Xenia".TrimEnd('\\') + '\\')
                : Path.Combine(_playniteApi.Paths.ApplicationPath, "ExtensionsData", SUCCESS_STORY_GUID, "Xenia".TrimEnd('\\') + '\\');
        }

        public override bool ValidateConfiguration()
        {
            if (CachedConfigurationValidationResult == null)
            {
                CachedConfigurationValidationResult = IsConfigured();

                if (!(bool)CachedConfigurationValidationResult)
                {
                    ShowNotificationPluginNoConfiguration();
                }
            }
            else if (!(bool)CachedConfigurationValidationResult)
            {
                ShowNotificationPluginErrorMessage(PlayniteTools.ExternalPlugin.SuccessStory);
            }

            return (bool)CachedConfigurationValidationResult;
        }

        public override bool IsConfigured()
        {
            if (string.IsNullOrEmpty(_xeniaProfilePath))
            {
                return false;
            }

            bool hasSuccessStoryDir = Directory.Exists(_successStoryDataDir);
            bool hasSuccessStoryXeniaDir = Directory.Exists(_successStoryXeniaDir);
            bool hasxboxTitleIDs = (xboxTitleIDs.Count != 0);
            return hasxboxTitleIDs && hasSuccessStoryDir && hasSuccessStoryXeniaDir;
        }

        public override bool EnabledInSettings()
        {
            return PluginDatabase.PluginSettings.Settings.EnableXbox360Achievements;
        }

        #endregion

        #region TitleID
        private void LoadTitleIDs()
        {
            try
            {
                using (StreamReader sr = new StreamReader($"{PluginDatabase.Paths.PluginPath}\\Resources\\Xbox360\\TitleIDs.json"))
                {
                    xboxTitleIDs = Serialization.FromJson<Dictionary<string, List<string>>>(sr.ReadToEnd());
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Xbox360: Error loading titleIDs: {ex.Message}");
                Common.LogError(ex, false, true, PluginDatabase.PluginName);
            }
        }

        public string FindAndVerifyTitleID(string GameName)
        {
            try
            {
                if (xboxTitleIDs.ContainsKey(GameName))
                {
                    foreach (var id in xboxTitleIDs[GameName])
                    {
                        if (File.Exists($"{_xeniaProfilePath}\\{id}.gpd"))
                        {
                            return id;
                        }
                    }

                    _logger.Error($"Xbox360: {GameName} gpd file not found! (Has the game been launched before?)");
                    return null;
                }
                _logger.Error($"Xbox360: {GameName} doesnt match any name in title keys json!");
                return null;
            }
            catch (Exception)
            {
                _logger.Error($"Xbox360: No titleID found for {GameName}");
                return null;
            }
        }

        #endregion

        #region GPD
        UInt16 GPDFile_ReadUInt16(byte[] data, Int32 index, out Int32 outIndex)
        {
            outIndex = index + 2;
            return BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt16(data, index));
        }
        UInt32 GPDFile_ReadUInt32(byte[] data, Int32 index, out Int32 outIndex)
        {
            outIndex = index + 4;
            return BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt32(data, index));
        }
        UInt64 GPDFile_ReadUInt64(byte[] data, Int32 index, out Int32 outIndex)
        {
            outIndex = index + 8;
            return BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt64(data, index));
        }

        public List<Achievement> LoadGPD(string path, string TitleID)
        {
            byte[] gpdFile = File.ReadAllBytes(path);
            Int32 index = 0;

            XdbfHeader header;
            List<XdbfEntry> entries = new List<XdbfEntry>();

            header = new XdbfHeader();
            header.magic = GPDFile_ReadUInt32(gpdFile, index, out index);
            header.version = GPDFile_ReadUInt32(gpdFile, index, out index);
            header.entry_count = GPDFile_ReadUInt32(gpdFile, index, out index);
            header.entry_used = GPDFile_ReadUInt32(gpdFile, index, out index);
            header.free_count = GPDFile_ReadUInt32(gpdFile, index, out index);
            header.free_used = GPDFile_ReadUInt32(gpdFile, index, out index);

            //Index to start of data
            Int32 freeIndex = index + (18 * (Int32)header.free_count);
            UInt32 dataIndex = (UInt32)freeIndex + (8 * header.free_count);

            //Load Data Entries
            for (var i = 0; i < header.entry_used; i++)
            {
                XdbfEntry entry = new XdbfEntry();
                entry.section = GPDFile_ReadUInt16(gpdFile, index, out index);
                entry.id = GPDFile_ReadUInt64(gpdFile, index, out index);
                entry.offset = GPDFile_ReadUInt32(gpdFile, index, out index);
                entry.size = GPDFile_ReadUInt32(gpdFile, index, out index);

                entry.data = new byte[entry.size];
                Array.Copy(gpdFile, dataIndex + entry.offset, entry.data, 0, entry.size);

                entries.Add(entry);
            }

            List<Achievement> achievements = new List<Achievement>();
            Directory.CreateDirectory($"{_playniteApi.Paths.ExtensionsDataPath}\\{SUCCESS_STORY_GUID}\\Xenia\\{TitleID}");

            foreach (var entry in entries)
            {
                switch (entry.section)
                {
                    //Achievement Data
                    case 1:
                        var localdataIndex = 0;

                        Achievement achievement = new Achievement();

                        BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt32(entry.data, localdataIndex)); localdataIndex += 4; //Magic Number
                        BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt32(entry.data, localdataIndex)); localdataIndex += 4; //ID

                        UInt32 imageid = BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt32(entry.data, localdataIndex)); localdataIndex += 4; //ImageID
                        achievement.UrlUnlocked = $"{_successStoryXeniaDir}\\{TitleID}\\{imageid}.png";
                        achievement.UrlLocked = $"{PluginDatabase.Paths.PluginPath}\\Resources\\Xbox360\\lock.png";

                        achievement.GamerScore = BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt32(entry.data, localdataIndex)); localdataIndex += 4; //Gamerscore
                        achievement.Percent = 100 - Math.Min(Math.Max(achievement.GamerScore, 0), 99);

                        BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt32(entry.data, localdataIndex)); localdataIndex += 4; //Flags
                        Int64 UnlockTime = (Int64)(BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt64(entry.data, localdataIndex))); localdataIndex += 8; //Unlock Time

                        string name = "";
                        while (BitConverter.ToUInt16(entry.data, localdataIndex) != 0)
                        {
                            name += ((char)BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt16(entry.data, localdataIndex))).ToString();
                            localdataIndex += 2;
                        }
                        localdataIndex += 2;

                        achievement.Name = name;

                        while (BitConverter.ToUInt16(entry.data, localdataIndex) != 0)
                        {
                            //Description when achievement is unlocked
                            achievement.Description += ((char)BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt16(entry.data, localdataIndex))).ToString();
                            localdataIndex += 2;
                        }
                        localdataIndex += 2;

                        if (UnlockTime != 0)
                        {
                            achievement.DateUnlocked = DateTime.FromFileTime(UnlockTime);
                        }
                        else
                        {
                            achievement.Description = null;
                            while (BitConverter.ToUInt16(entry.data, localdataIndex) != 0)
                            {
                                //Description when achievement is locked
                                achievement.Description += ((char)BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt16(entry.data, localdataIndex))).ToString();
                                localdataIndex += 2;
                            }
                        }

                        achievements.Add(achievement);
                        break;

                    //Icon Data
                    case 2:
                        using (var fs = new FileStream($"{_successStoryXeniaDir}\\{TitleID}\\{entry.id}.png", FileMode.Create, FileAccess.Write))
                        {
                            fs.Write(entry.data, 0, (Int32)entry.size);
                        }
                        break;

                    default:
                        break;
                }
            }

            return achievements;
        }

        #endregion       
        
    }
}
