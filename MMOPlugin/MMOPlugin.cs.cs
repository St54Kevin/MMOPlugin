using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GameReaderCommon;
using SimHub.Plugins;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace MMOPlugin
{
    [PluginName("MMO Plugin")]
    [PluginAuthor("Kevin_Karas")]
    [PluginDescription("Extended Le Mans Ultimate standings data.")]
    public class MMOPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        public const string PluginVersion = "1.2.2.9";
        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SimHub",
            "PluginsData",
            "MMOPlugin",
            "settings.ini"
        );

        public PluginManager PluginManager { get; set; }

        public string LeftMenuTitle
        {
            get { return "MMO Plugin"; }
        }

        public ImageSource PictureIcon
        {
            get { return null; }
        }

        private const int MaxVehicles = 62;

        private readonly LMUSharedMemory _sharedMemory =
            new LMUSharedMemory();

        private readonly LMUStandingsApi _standingsApi =
            new LMUStandingsApi();

        private readonly LMUVehicleScoring[] _vehiclesByPosition =
            new LMUVehicleScoring[MaxVehicles];

        private readonly bool[] _hasVehicle =
            new bool[MaxVehicles];

        private LMUScoringInfo _scoringInfo;

        private bool _hasScoringInfo;
        private volatile bool _showTeamNamesInsteadOfDriverNames;
        private volatile bool _additionalTeamNamesInRelative;
        private volatile int _tyreCompoundSymbolMode;

        private readonly string[] _relativeAhead = new string[3];
        private readonly string[] _relativeBehind = new string[3];
        private readonly int[] _incidentsByPosition = new int[MaxVehicles];
        private readonly string[][] _tyreCompoundsByPosition =
            new string[MaxVehicles][];
        private readonly Dictionary<int, LastLapTimingState> _lastLapTimingByVehicleId =
            new Dictionary<int, LastLapTimingState>();
        private int _playerIncidents = -1;

        private const int MaxMappedVehicles = 104;

        private sealed class LastLapTimingState
        {
            public bool HasLapStart;
            public double LapStartET;
            public double LastLapTime;
        }

        public void Init(PluginManager pluginManager)
        {
            PluginManager = pluginManager;
            LoadPluginSettings();

            _standingsApi.Start();
            ClearTrackLimitPoints();

            this.AttachDelegate(
                name: "MMO_ConnectionStatus",
                valueProvider: () =>
                    _sharedMemory.IsConnected
                        ? "Connected to LMU"
                        : "Waiting for LMU"
            );

            this.AttachDelegate(
                name: "MMO_TrackName",
                valueProvider: () =>
                    _hasScoringInfo
                        ? DecodeString(_scoringInfo.mTrackName)
                        : ""
            );

            this.AttachDelegate(
                name: "MMO_VehicleCount",
                valueProvider: () =>
                    _hasScoringInfo
                        ? _scoringInfo.mNumVehicles
                        : 0
            );

            this.AttachDelegate(
                name: "MMO_ApiConnectionStatus",
                valueProvider: () =>
                    _standingsApi.IsConnected
                        ? "Connected to LMU API"
                        : "Waiting for LMU API"
            );

            this.AttachDelegate(
                name: "MMO_ApiLastError",
                valueProvider: () =>
                    _standingsApi.LastError
            );

            this.AttachDelegate(
                name: "MMO_SettingsShowTeamNamesInLeaderboard",
                valueProvider: () => _showTeamNamesInsteadOfDriverNames ? 1 : 0
            );

            this.AttachDelegate(
                name: "MMO_SettingsAdditionalTeamNamesInRelative",
                valueProvider: () => _additionalTeamNamesInRelative ? 1 : 0
            );

            this.AttachDelegate(
                name: "MMO_SettingsTyreCompoundSymbol",
                valueProvider: () => _tyreCompoundSymbolMode
            );

            this.AttachDelegate(
                name: "MMO_InvalidLap",
                valueProvider: () =>
                {
                    for (int positionIndex = 0;
                         positionIndex < MaxVehicles;
                         positionIndex++)
                    {
                        if (!_hasVehicle[positionIndex])
                            continue;

                        LMUVehicleScoring vehicle =
                            _vehiclesByPosition[positionIndex];

                        if (!vehicle.mIsPlayer)
                            continue;

                        return vehicle.mLastLapTime < 0.0 ? 1 : 0;
                    }

                    return 0;
                }
            );

            this.AttachDelegate(
                name: "MMO_PlayerInc",
                valueProvider: () => _playerIncidents
            );

            // -1 means the session's configured track-limit threshold is not available
            // through the shared-memory fields currently mapped by this plugin.
            this.AttachDelegate(
                name: "MMO_maxIncidents",
                valueProvider: () => -1
            );

            this.AttachDelegate(
                name: "MMO_RelativeAhead1",
                valueProvider: () => _relativeAhead[0]
            );

            this.AttachDelegate(
                name: "MMO_RelativeAhead2",
                valueProvider: () => _relativeAhead[1]
            );

            this.AttachDelegate(
                name: "MMO_RelativeAhead3",
                valueProvider: () => _relativeAhead[2]
            );

            this.AttachDelegate(
                name: "MMO_RelativeBehind1",
                valueProvider: () => _relativeBehind[0]
            );

            this.AttachDelegate(
                name: "MMO_RelativeBehind2",
                valueProvider: () => _relativeBehind[1]
            );

            this.AttachDelegate(
                name: "MMO_RelativeBehind3",
                valueProvider: () => _relativeBehind[2]
            );

            RegisterPlayerProperties();

            for (int position = 1;
                 position <= MaxVehicles;
                 position++)
            {
                int capturedPosition = position;

                string positionName =
                    capturedPosition.ToString(CultureInfo.InvariantCulture);

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "Overall",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null
                            ? vehicle.Position
                            : 0;
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "Class",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null
                            ? vehicle.PositionClass
                            : 0;
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "CarNumber",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null
                            ? vehicle.CarNumber ?? ""
                            : "";
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "CarClass",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null
                            ? vehicle.CarClass ?? ""
                            : "";
                    }
                );

                this.AttachDelegate(
                    name: "MMO_Position" + positionName + "TyreCompFL",
                    valueProvider: () => GetTyreCompound(capturedPosition, 0)
                );

                this.AttachDelegate(
                    name: "MMO_Position" + positionName + "TyreCompFR",
                    valueProvider: () => GetTyreCompound(capturedPosition, 1)
                );

                this.AttachDelegate(
                    name: "MMO_Position" + positionName + "TyreCompRL",
                    valueProvider: () => GetTyreCompound(capturedPosition, 2)
                );

                this.AttachDelegate(
                    name: "MMO_Position" + positionName + "TyreCompRR",
                    valueProvider: () => GetTyreCompound(capturedPosition, 3)
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "DriverName",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        if (vehicle == null)
                            return "";

                        return _showTeamNamesInsteadOfDriverNames
                            ? vehicle.TeamName ?? ""
                            : FormatDriverName(vehicle.DriverName);
                    }
                );

                this.AttachDelegate(
                    name: "MMO_Position" + positionName + "DriveNameRelative",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(capturedPosition);

                        if (vehicle == null)
                            return "";

                        string driverName = vehicle.DriverName ?? "";
                        return _additionalTeamNamesInRelative
                            ? FormatRelativeDriverName(driverName, vehicle.TeamName)
                            : driverName;
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "TeamName",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null
                            ? vehicle.TeamName ?? ""
                            : "";
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "BestLaptime",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null
                            ? vehicle.BestLapTime
                            : 0.0;
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "LastLaptime",
                    valueProvider: () =>
                    {
                        int positionIndex = capturedPosition - 1;

                        if (positionIndex < 0 ||
                            positionIndex >= MaxVehicles ||
                            !_hasVehicle[positionIndex])
                        {
                            return FormatLapTime(0.0);
                        }

                        LMUVehicleScoring scoringVehicle =
                            _vehiclesByPosition[positionIndex];

                        double validLastLap = scoringVehicle.mLastLapTime;

                        if (validLastLap > 0.0 &&
                            !double.IsNaN(validLastLap) &&
                            !double.IsInfinity(validLastLap))
                        {
                            return FormatLapTime(validLastLap);
                        }

                        LastLapTimingState timingState;

                        if (_lastLapTimingByVehicleId.TryGetValue(
                                scoringVehicle.mID,
                                out timingState) &&
                            timingState.LastLapTime > 0.0 &&
                            !double.IsNaN(timingState.LastLapTime) &&
                            !double.IsInfinity(timingState.LastLapTime))
                        {
                            return FormatLapTime(timingState.LastLapTime);
                        }

                        LMUApiVehicle apiVehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        double apiLastLap = apiVehicle != null
                            ? apiVehicle.LastLapTime
                            : 0.0;

                        if (apiLastLap > 0.0 &&
                            !double.IsNaN(apiLastLap) &&
                            !double.IsInfinity(apiLastLap))
                        {
                            return FormatLapTime(apiLastLap);
                        }

                        return FormatLapTime(0.0);
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "GapToLeader",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        if (vehicle == null)
                            return "";

                        int lapsBehindClassLeader =
                            GetLapsBehindClassLeader(
                                capturedPosition,
                                vehicle
                            );

                        return FormatGap(
                            vehicle.GapToLeader,
                            lapsBehindClassLeader
                        );
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "IntervalToNext",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        if (vehicle == null)
                            return "";

                        int lapsBehindNext =
                            GetLapsBehindNext(
                                capturedPosition
                            );

                        return FormatGap(
                            vehicle.IntervalToNext,
                            lapsBehindNext
                        );
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "PitStatus",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        if (vehicle == null)
                            return "onTrack";

                        if (vehicle.InGarageStall)
                            return "InGarage";

                        if (vehicle.Pitting)
                            return "InPit";

                        if (!string.IsNullOrEmpty(
                                vehicle.PitState) &&
                            !string.Equals(
                                vehicle.PitState,
                                "NONE",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return "inPitLane";
                        }

                        return "onTrack";
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "VirtualEnergy",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        if (vehicle == null)
                            return "";

                        return FormatVirtualEnergy(
                            vehicle.VeFraction,
                            vehicle.FuelFraction
                        );
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "Penalties",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null
                            ? vehicle.Penalties
                            : 0;
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "Incidents",
                    valueProvider: () =>
                        _incidentsByPosition[capturedPosition - 1]
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "InvalidLap",
                    valueProvider: () =>
                    {
                        int positionIndex = capturedPosition - 1;

                        if (positionIndex < 0 ||
                            positionIndex >= MaxVehicles ||
                            !_hasVehicle[positionIndex])
                        {
                            return 0;
                        }

                        return _vehiclesByPosition[positionIndex].mLastLapTime < 0.0
                            ? 1
                            : 0;
                    }
                );

                this.AttachDelegate(
                    name:
                        "MMO_Position" +
                        positionName +
                        "FinishStatus",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null &&
                               !string.IsNullOrEmpty(vehicle.FinishStatus) &&
                               !string.Equals(
                                   vehicle.FinishStatus,
                                   "FSTAT_NONE",
                                   StringComparison.OrdinalIgnoreCase
                               )
                            ? 1
                            : 0;
                    }
                );
            }
        }

        private bool TryGetPlayerScoring(
            out int position,
            out LMUVehicleScoring scoringVehicle)
        {
            for (int i = 0; i < MaxVehicles; i++)
            {
                if (_hasVehicle[i] && _vehiclesByPosition[i].mIsPlayer)
                {
                    position = i + 1;
                    scoringVehicle = _vehiclesByPosition[i];
                    return true;
                }
            }

            position = 0;
            scoringVehicle = new LMUVehicleScoring();
            return false;
        }

        private LMUApiVehicle GetPlayerApiVehicle(out int position)
        {
            LMUVehicleScoring ignored;
            if (!TryGetPlayerScoring(out position, out ignored))
                return null;

            return _standingsApi.GetVehicleByPosition(position);
        }

        private void RegisterPlayerProperties()
        {
            this.AttachDelegate("MMO_PlayerRelativeAhead1", () => _relativeAhead[0]);
            this.AttachDelegate("MMO_PlayerRelativeAhead2", () => _relativeAhead[1]);
            this.AttachDelegate("MMO_PlayerRelativeAhead3", () => _relativeAhead[2]);
            this.AttachDelegate("MMO_PlayerRelativeBehind1", () => _relativeBehind[0]);
            this.AttachDelegate("MMO_PlayerRelativeBehind2", () => _relativeBehind[1]);
            this.AttachDelegate("MMO_PlayerRelativeBehind3", () => _relativeBehind[2]);

            this.AttachDelegate("MMO_PlayerOverall", () =>
            {
                int position;
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out position);
                return vehicle != null ? vehicle.Position : position;
            });
            this.AttachDelegate("MMO_PlayerClass", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null ? vehicle.PositionClass : 0;
            });
            this.AttachDelegate("MMO_PlayerCarNumber", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null ? vehicle.CarNumber ?? "" : "";
            });
            this.AttachDelegate("MMO_PlayerCarClass", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null ? vehicle.CarClass ?? "" : "";
            });

            string[] wheelNames = { "FL", "FR", "RL", "RR" };
            for (int wheel = 0; wheel < wheelNames.Length; wheel++)
            {
                int capturedWheel = wheel;
                this.AttachDelegate(
                    "MMO_PlayerTyreComp" + wheelNames[wheel],
                    () =>
                    {
                        int position;
                        return TryGetPlayerScoring(out position, out _)
                            ? GetTyreCompound(position, capturedWheel)
                            : "";
                    }
                );
            }

            this.AttachDelegate("MMO_PlayerDriverName", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                if (vehicle == null) return "";
                return _showTeamNamesInsteadOfDriverNames
                    ? vehicle.TeamName ?? ""
                    : FormatDriverName(vehicle.DriverName);
            });
            this.AttachDelegate("MMO_PlayerDriveNameRelative", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                if (vehicle == null) return "";
                string driverName = vehicle.DriverName ?? "";
                return _additionalTeamNamesInRelative
                    ? FormatRelativeDriverName(driverName, vehicle.TeamName)
                    : driverName;
            });
            this.AttachDelegate("MMO_PlayerTeamName", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null ? vehicle.TeamName ?? "" : "";
            });
            this.AttachDelegate("MMO_PlayerBestLaptime", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null ? vehicle.BestLapTime : 0.0;
            });
            this.AttachDelegate("MMO_PlayerLastLaptime", () =>
            {
                int position;
                LMUVehicleScoring scoring;
                if (!TryGetPlayerScoring(out position, out scoring))
                    return FormatLapTime(0.0);

                double lastLap = scoring.mLastLapTime;
                if (lastLap > 0.0 && !double.IsNaN(lastLap) && !double.IsInfinity(lastLap))
                    return FormatLapTime(lastLap);

                LastLapTimingState timing;
                if (_lastLapTimingByVehicleId.TryGetValue(scoring.mID, out timing) &&
                    timing.LastLapTime > 0.0 && !double.IsNaN(timing.LastLapTime) &&
                    !double.IsInfinity(timing.LastLapTime))
                    return FormatLapTime(timing.LastLapTime);

                LMUApiVehicle vehicle = _standingsApi.GetVehicleByPosition(position);
                lastLap = vehicle != null ? vehicle.LastLapTime : 0.0;
                return lastLap > 0.0 && !double.IsNaN(lastLap) && !double.IsInfinity(lastLap)
                    ? FormatLapTime(lastLap)
                    : FormatLapTime(0.0);
            });
            this.AttachDelegate("MMO_PlayerGapToLeader", () =>
            {
                int position;
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out position);
                return vehicle != null
                    ? FormatGap(vehicle.GapToLeader, GetLapsBehindClassLeader(position, vehicle))
                    : "";
            });
            this.AttachDelegate("MMO_PlayerIntervalToNext", () =>
            {
                int position;
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out position);
                return vehicle != null
                    ? FormatGap(vehicle.IntervalToNext, GetLapsBehindNext(position))
                    : "";
            });
            this.AttachDelegate("MMO_PlayerPitStatus", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                if (vehicle == null) return "onTrack";
                if (vehicle.InGarageStall) return "InGarage";
                if (vehicle.Pitting) return "InPit";
                return !string.IsNullOrEmpty(vehicle.PitState) &&
                       !string.Equals(vehicle.PitState, "NONE", StringComparison.OrdinalIgnoreCase)
                    ? "inPitLane"
                    : "onTrack";
            });
            this.AttachDelegate("MMO_PlayerVirtualEnergy", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null
                    ? FormatVirtualEnergy(vehicle.VeFraction, vehicle.FuelFraction)
                    : "";
            });
            this.AttachDelegate("MMO_PlayerPenalties", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null ? vehicle.Penalties : 0;
            });
            this.AttachDelegate("MMO_PlayerIncidents", () => _playerIncidents);
            this.AttachDelegate("MMO_PlayerInvalidLap", () =>
            {
                int position;
                LMUVehicleScoring scoring;
                return TryGetPlayerScoring(out position, out scoring) && scoring.mLastLapTime < 0.0
                    ? 1
                    : 0;
            });
            this.AttachDelegate("MMO_PlayerFinishStatus", () =>
            {
                LMUApiVehicle vehicle = GetPlayerApiVehicle(out _);
                return vehicle != null && !string.IsNullOrEmpty(vehicle.FinishStatus) &&
                       !string.Equals(vehicle.FinishStatus, "FSTAT_NONE", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 0;
            });
        }

        public Control GetWPFSettingsControl(
            PluginManager pluginManager)
        {
            return new MMOPluginSettingsControl(
                pluginManager,
                PluginVersion,
                BuildPropertyNames(),
                _showTeamNamesInsteadOfDriverNames,
                SetShowTeamNamesInLeaderboard,
                _additionalTeamNamesInRelative,
                SetAdditionalTeamNamesInRelative,
                _tyreCompoundSymbolMode,
                SetTyreCompoundSymbolMode
            );
        }

        private void LoadPluginSettings()
        {
            _showTeamNamesInsteadOfDriverNames = false;
            _additionalTeamNamesInRelative = false;
            _tyreCompoundSymbolMode = 0;
            try
            {
                if (!File.Exists(SettingsFilePath))
                    return;

                string[] lines = File.ReadAllLines(SettingsFilePath);
                bool legacyValue;
                if (lines.Length == 1 && bool.TryParse(lines[0].Trim(), out legacyValue))
                {
                    _showTeamNamesInsteadOfDriverNames = legacyValue;
                    return;
                }

                foreach (string line in lines)
                {
                    int separator = line.IndexOf('=');
                    if (separator < 0)
                        continue;

                    string key = line.Substring(0, separator).Trim();
                    string settingValue = line.Substring(separator + 1).Trim();
                    if (string.Equals(key, "TyreCompoundSymbol", StringComparison.OrdinalIgnoreCase))
                    {
                        int mode;
                        if (int.TryParse(settingValue, out mode) && mode >= 0 && mode <= 2)
                            _tyreCompoundSymbolMode = mode;
                        continue;
                    }

                    bool value;
                    if (!bool.TryParse(settingValue, out value))
                        continue;

                    if (string.Equals(key, "ShowTeamNamesInLeaderboard", StringComparison.OrdinalIgnoreCase))
                        _showTeamNamesInsteadOfDriverNames = value;
                    else if (string.Equals(key, "AdditionalTeamNamesInRelative", StringComparison.OrdinalIgnoreCase))
                        _additionalTeamNamesInRelative = value;
                }
            }
            catch
            {
                _showTeamNamesInsteadOfDriverNames = false;
                _additionalTeamNamesInRelative = false;
                _tyreCompoundSymbolMode = 0;
            }
        }

        private void SetShowTeamNamesInLeaderboard(bool value)
        {
            _showTeamNamesInsteadOfDriverNames = value;
            SavePluginSettings();
        }

        private void SetAdditionalTeamNamesInRelative(bool value)
        {
            _additionalTeamNamesInRelative = value;
            SavePluginSettings();
        }

        private void SetTyreCompoundSymbolMode(int value)
        {
            _tyreCompoundSymbolMode = value >= 0 && value <= 2 ? value : 0;
            SavePluginSettings();
        }

        private void SavePluginSettings()
        {
            try
            {
                string directory = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllLines(SettingsFilePath, new[]
                {
                    "ShowTeamNamesInLeaderboard=" + _showTeamNamesInsteadOfDriverNames,
                    "AdditionalTeamNamesInRelative=" + _additionalTeamNamesInRelative,
                    "TyreCompoundSymbol=" + _tyreCompoundSymbolMode
                });
            }
            catch
            {
                // Keep the option active for this session when settings cannot be saved.
            }
        }

        private static List<string> BuildPropertyNames()
        {
            List<string> names = new List<string>
            {
                "MMO_ConnectionStatus",
                "MMO_TrackName",
                "MMO_VehicleCount",
                "MMO_ApiConnectionStatus",
                "MMO_ApiLastError",
                "MMO_SettingsShowTeamNamesInLeaderboard",
                "MMO_SettingsAdditionalTeamNamesInRelative",
                "MMO_SettingsTyreCompoundSymbol",
                "MMO_InvalidLap",
                "MMO_PlayerInc",
                "MMO_maxIncidents",
                "MMO_RelativeAhead1",
                "MMO_RelativeAhead2",
                "MMO_RelativeAhead3",
                "MMO_RelativeBehind1",
                "MMO_RelativeBehind2",
                "MMO_RelativeBehind3"
            };

            string[] playerSuffixes =
            {
                "Overall", "Class", "CarNumber", "CarClass",
                "TyreCompFL", "TyreCompFR", "TyreCompRL", "TyreCompRR",
                "DriverName", "DriveNameRelative", "TeamName",
                "BestLaptime", "LastLaptime", "GapToLeader",
                "IntervalToNext", "PitStatus", "VirtualEnergy",
                "Penalties", "Incidents", "InvalidLap", "FinishStatus"
            };

            foreach (string suffix in playerSuffixes)
                names.Add("MMO_Player" + suffix);

            for (int i = 1; i <= 3; i++)
            {
                names.Add("MMO_PlayerRelativeAhead" + i);
                names.Add("MMO_PlayerRelativeBehind" + i);
            }

            string[] positionSuffixes =
            {
                "Overall",
                "Class",
                "CarNumber",
                "CarClass",
                "TyreCompFL",
                "TyreCompFR",
                "TyreCompRL",
                "TyreCompRR",
                "DriverName",
                "DriveNameRelative",
                "TeamName",
                "BestLaptime",
                "LastLaptime",
                "GapToLeader",
                "IntervalToNext",
                "PitStatus",
                "VirtualEnergy",
                "Penalties",
                "Incidents",
                "InvalidLap",
                "FinishStatus"
            };

            for (int position = 1;
                 position <= MaxVehicles;
                 position++)
            {
                string positionName =
                    position.ToString(CultureInfo.InvariantCulture);

                foreach (string suffix in positionSuffixes)
                {
                    names.Add(
                        "MMO_Position" +
                        positionName +
                        suffix
                    );
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        public void DataUpdate(
            PluginManager pluginManager,
            ref GameData data)
        {
            _sharedMemory.UpdateConnection();

            if (!_sharedMemory.IsConnected)
            {
                ClearRelativeGaps();
                ClearTrackLimitPoints();
                return;
            }

            _hasScoringInfo =
                _sharedMemory.TryReadScoringInfo(
                    out _scoringInfo
                );

            if (!_hasScoringInfo)
            {
                ClearRelativeGaps();
                ClearTrackLimitPoints();
                return;
            }

            for (int i = 0;
                 i < MaxVehicles;
                 i++)
            {
                _hasVehicle[i] = false;

                _vehiclesByPosition[i] =
                    new LMUVehicleScoring();
            }

            if (!_hasScoringInfo)
                return;

            _standingsApi.SetSession(
                _scoringInfo.mSession
            );

            int vehicleCount =
                _scoringInfo.mNumVehicles;

            if (vehicleCount < 0)
                vehicleCount = 0;

            if (vehicleCount > MaxVehicles)
                vehicleCount = MaxVehicles;

            for (int vehicleIndex = 0;
                 vehicleIndex < vehicleCount;
                 vehicleIndex++)
            {
                LMUVehicleScoring vehicle;

                if (!_sharedMemory.TryReadVehicleScoring(
                        vehicleIndex,
                        out vehicle))
                {
                    continue;
                }

                int place =
                    vehicle.mPlace;

                if (place < 1 ||
                    place > MaxVehicles)
                {
                    continue;
                }

                int positionIndex =
                    place - 1;

                _vehiclesByPosition[positionIndex] =
                    vehicle;

                _hasVehicle[positionIndex] =
                    true;
            }

            UpdateRelativeGaps(vehicleCount);
            UpdateTrackLimitPoints();
            UpdateLastLapTimes();
        }

        private void UpdateLastLapTimes()
        {
            for (int positionIndex = 0;
                 positionIndex < MaxVehicles;
                 positionIndex++)
            {
                if (!_hasVehicle[positionIndex])
                    continue;

                LMUVehicleScoring vehicle =
                    _vehiclesByPosition[positionIndex];

                if (vehicle.mID < 0)
                    continue;

                double currentLapStartET = vehicle.mLapStartET;

                if (double.IsNaN(currentLapStartET) ||
                    double.IsInfinity(currentLapStartET))
                {
                    continue;
                }

                LastLapTimingState state;

                if (!_lastLapTimingByVehicleId.TryGetValue(
                        vehicle.mID,
                        out state))
                {
                    state = new LastLapTimingState();
                    _lastLapTimingByVehicleId.Add(
                        vehicle.mID,
                        state
                    );
                }

                if (currentLapStartET <= 0.0)
                {
                    state.HasLapStart = false;
                    state.LapStartET = currentLapStartET;
                    state.LastLapTime = 0.0;
                    continue;
                }

                if (!state.HasLapStart)
                {
                    state.LapStartET = currentLapStartET;
                    state.HasLapStart = true;
                    continue;
                }

                if (currentLapStartET > state.LapStartET + 0.001)
                {
                    double completedLapTime =
                        currentLapStartET - state.LapStartET;

                    if (completedLapTime > 0.0 &&
                        completedLapTime <= 3600.0)
                    {
                        state.LastLapTime = completedLapTime;
                    }

                    state.LapStartET = currentLapStartET;
                }
                else if (currentLapStartET < state.LapStartET - 0.001)
                {
                    // Session reset or a vehicle slot/lap timer reset.
                    state.LapStartET = currentLapStartET;
                    state.LastLapTime = 0.0;
                    state.HasLapStart = true;
                }
            }
        }

        private void UpdateTrackLimitPoints()
        {
            ClearTrackLimitPoints();

            Dictionary<int, LMUVehicleTelemetryRelative> telemetryById =
                new Dictionary<int, LMUVehicleTelemetryRelative>();

            for (int telemetryIndex = 0; telemetryIndex < 104; telemetryIndex++)
            {
                LMUVehicleTelemetryRelative telemetry;

                if (!_sharedMemory.TryReadVehicleTelemetryRelative(
                        telemetryIndex,
                        out telemetry))
                {
                    continue;
                }

                if (telemetry.mID >= 0 &&
                    !telemetryById.ContainsKey(telemetry.mID))
                {
                    telemetryById.Add(telemetry.mID, telemetry);
                }
            }

            for (int positionIndex = 0; positionIndex < MaxVehicles; positionIndex++)
            {
                if (!_hasVehicle[positionIndex])
                    continue;

                LMUVehicleScoring vehicle = _vehiclesByPosition[positionIndex];
                LMUVehicleTelemetryRelative telemetry;

                if (!telemetryById.TryGetValue(vehicle.mID, out telemetry))
                    continue;

                // LMU exposes normalized track-limit steps in this telemetry byte.
                // Keep the raw value for in-game comparison; scale can be confirmed empirically.
                _incidentsByPosition[positionIndex] = telemetry.mTrackLimitsSteps;

                _tyreCompoundsByPosition[positionIndex] = new[]
                {
                    FormatTyreCompound(telemetry.mTyreCompFL),
                    FormatTyreCompound(telemetry.mTyreCompFR),
                    FormatTyreCompound(telemetry.mTyreCompRL),
                    FormatTyreCompound(telemetry.mTyreCompRR)
                };

                if (vehicle.mIsPlayer)
                    _playerIncidents = telemetry.mTrackLimitsSteps;
            }
        }

        private void ClearTrackLimitPoints()
        {
            _playerIncidents = -1;

            for (int i = 0; i < MaxVehicles; i++)
            {
                _incidentsByPosition[i] = -1;
                _tyreCompoundsByPosition[i] = null;
            }
        }

        private string GetTyreCompound(
            int position,
            int wheelIndex)
        {
            int positionIndex = position - 1;

            if (positionIndex < 0 ||
                positionIndex >= MaxVehicles ||
                wheelIndex < 0 ||
                wheelIndex >= 4 ||
                _tyreCompoundsByPosition[positionIndex] == null)
            {
                return "";
            }

            return _tyreCompoundsByPosition[positionIndex][wheelIndex];
        }

        private static string FormatTyreCompound(
            byte compoundType)
        {
            switch (compoundType)
            {
                case 0:
                    return "Soft";
                case 1:
                    return "Medium";
                case 2:
                    return "Hard";
                case 3:
                    return "Wet";
                default:
                    return "";
            }
        }

        private void UpdateRelativeGaps(int vehicleCount)
        {
            ClearRelativeGaps();

            double trackLength = _scoringInfo.mLapDist;

            if (trackLength <= 0.0 ||
                double.IsNaN(trackLength) ||
                double.IsInfinity(trackLength))
            {
                return;
            }

            Dictionary<int, LMUVehicleTelemetryRelative> telemetryById =
                new Dictionary<int, LMUVehicleTelemetryRelative>();

            for (int telemetryIndex = 0; telemetryIndex < 104; telemetryIndex++)
            {
                LMUVehicleTelemetryRelative telemetry;

                if (!_sharedMemory.TryReadVehicleTelemetryRelative(
                        telemetryIndex,
                        out telemetry))
                {
                    continue;
                }

                if (telemetry.mID >= 0 &&
                    !telemetryById.ContainsKey(telemetry.mID))
                {
                    telemetryById.Add(telemetry.mID, telemetry);
                }
            }

            List<LMUVehicleScoring> vehicles = new List<LMUVehicleScoring>();
            LMUVehicleScoring player = new LMUVehicleScoring();
            bool hasPlayer = false;

            for (int vehicleIndex = 0;
                 vehicleIndex < vehicleCount;
                 vehicleIndex++)
            {
                LMUVehicleScoring vehicle;

                if (!_sharedMemory.TryReadVehicleScoring(
                        vehicleIndex,
                        out vehicle))
                {
                    continue;
                }

                vehicles.Add(vehicle);

                if (vehicle.mIsPlayer)
                {
                    player = vehicle;
                    hasPlayer = true;
                }
            }

            if (!hasPlayer)
                return;

            List<RelativeVehicle> ahead = new List<RelativeVehicle>();
            List<RelativeVehicle> behind = new List<RelativeVehicle>();

            foreach (LMUVehicleScoring vehicle in vehicles)
            {
                if (vehicle.mIsPlayer ||
                    vehicle.mInPits ||
                    vehicle.mInGarageStall ||
                    vehicle.mLapDist < 0.0 ||
                    vehicle.mLapDist >= trackLength)
                {
                    continue;
                }

                double distanceAhead = vehicle.mLapDist - player.mLapDist;

                if (distanceAhead < 0.0)
                    distanceAhead += trackLength;

                double distanceBehind = player.mLapDist - vehicle.mLapDist;

                if (distanceBehind < 0.0)
                    distanceBehind += trackLength;

                if (distanceAhead > 0.5)
                {
                    ahead.Add(new RelativeVehicle
                    {
                        Scoring = vehicle,
                        Distance = distanceAhead
                    });
                }

                if (distanceBehind > 0.5)
                {
                    behind.Add(new RelativeVehicle
                    {
                        Scoring = vehicle,
                        Distance = distanceBehind
                    });
                }
            }

            ahead.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            behind.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            LMUVehicleTelemetryRelative playerTelemetry;

            if (!telemetryById.TryGetValue(player.mID, out playerTelemetry))
                return;

            double cumulativeGap = GetValidRelativeGap(
                playerTelemetry.mTimeGapCarAhead);

            for (int i = 0; i < 3 && i < ahead.Count; i++)
            {
                if (i > 0)
                {
                    LMUVehicleTelemetryRelative previousAheadTelemetry;

                    if (!telemetryById.TryGetValue(
                            ahead[i - 1].Scoring.mID,
                            out previousAheadTelemetry))
                    {
                        break;
                    }

                    double nextGap = GetValidRelativeGap(
                        previousAheadTelemetry.mTimeGapCarAhead);

                    if (double.IsNaN(nextGap))
                        break;

                    cumulativeGap += nextGap;
                }

                if (double.IsNaN(cumulativeGap) ||
                    cumulativeGap > 180.0)
                {
                    break;
                }

                _relativeAhead[i] = FormatRelativeGap(cumulativeGap);
            }

            cumulativeGap = GetValidRelativeGap(
                playerTelemetry.mTimeGapCarBehind);

            for (int i = 0; i < 3 && i < behind.Count; i++)
            {
                if (i > 0)
                {
                    LMUVehicleTelemetryRelative previousBehindTelemetry;

                    if (!telemetryById.TryGetValue(
                            behind[i - 1].Scoring.mID,
                            out previousBehindTelemetry))
                    {
                        break;
                    }

                    double nextGap = GetValidRelativeGap(
                        previousBehindTelemetry.mTimeGapCarBehind);

                    if (double.IsNaN(nextGap))
                        break;

                    cumulativeGap += nextGap;
                }

                if (double.IsNaN(cumulativeGap) ||
                    cumulativeGap > 180.0)
                {
                    break;
                }

                _relativeBehind[i] = FormatRelativeGap(cumulativeGap);
            }
        }

        private void ClearRelativeGaps()
        {
            for (int i = 0; i < 3; i++)
            {
                _relativeAhead[i] = "";
                _relativeBehind[i] = "";
            }
        }

        private static double GetValidRelativeGap(double value)
        {
            if (double.IsNaN(value) ||
                double.IsInfinity(value))
            {
                return double.NaN;
            }

            value = Math.Abs(value);

            if (value <= 0.0 ||
                value > 180.0)
            {
                return double.NaN;
            }

            return value;
        }
        private static string FormatRelativeGap(double value)
        {
            if (double.IsNaN(value) ||
                double.IsInfinity(value) ||
                value <= 0.0 ||
                value > 180.0)
            {
                return "";
            }

            if (value >= 60.0)
            {
                return TimeSpan.FromSeconds(value).ToString(
                    @"m\:ss",
                    CultureInfo.InvariantCulture
                );
            }

            return value.ToString(
                "F1",
                CultureInfo.InvariantCulture
            );
        }

        private sealed class RelativeVehicle
        {
            public LMUVehicleScoring Scoring;
            public double Distance;
        }

        private static string FormatLapTime(double seconds)
        {
            if (double.IsNaN(seconds) ||
                double.IsInfinity(seconds) ||
                seconds <= 0.0 ||
                seconds > TimeSpan.MaxValue.TotalSeconds)
            {
                return "0";
            }

            return TimeSpan.FromSeconds(seconds).ToString(
                @"m\:ss\.fff",
                CultureInfo.InvariantCulture
            );
        }

        private int GetLapsBehindNext(
            int position)
        {
            if (!_hasScoringInfo ||
                _scoringInfo.mSession < 10 ||
                _scoringInfo.mSession > 13 ||
                position < 1 ||
                position > MaxVehicles ||
                !_hasVehicle[position - 1])
            {
                return 0;
            }

            int laps =
                _vehiclesByPosition[position - 1].mLapsBehindNext;

            return laps > 0 ? laps : 0;
        }

        private int GetLapsBehindClassLeader(
            int position,
            LMUApiVehicle vehicle)
        {
            if (!_hasScoringInfo ||
                _scoringInfo.mSession < 10 ||
                _scoringInfo.mSession > 13 ||
                vehicle == null ||
                vehicle.PositionClass <= 1 ||
                position < 1 ||
                position > MaxVehicles ||
                !_hasVehicle[position - 1])
            {
                return 0;
            }

            LMUApiVehicle classLeader = null;

            for (int overall = 1; overall <= MaxVehicles; overall++)
            {
                LMUApiVehicle candidate =
                    _standingsApi.GetVehicleByPosition(overall);

                if (candidate != null &&
                    candidate.PositionClass == 1 &&
                    string.Equals(
                        candidate.CarClass ?? "",
                        vehicle.CarClass ?? "",
                        StringComparison.OrdinalIgnoreCase))
                {
                    classLeader = candidate;
                    break;
                }
            }

            if (classLeader == null ||
                classLeader.Position < 1 ||
                classLeader.Position > MaxVehicles ||
                !_hasVehicle[classLeader.Position - 1])
            {
                return 0;
            }

            // LMU's laps-behind-leader counter only advances when a car has
            // actually been lapped. Compare the counter against the class
            // leader's counter; mTotalLaps can change at the start/finish line
            // before the leader has truly lapped this car.
            int lapsBehindVehicle =
                _vehiclesByPosition[position - 1].mLapsBehindLeader;
            int lapsBehindClassLeader =
                _vehiclesByPosition[classLeader.Position - 1].mLapsBehindLeader;

            int laps =
                lapsBehindVehicle - lapsBehindClassLeader;

            return laps > 0 ? laps : 0;
        }

        private string FormatGap(
            double value,
            int lapsBehind = 0)
        {
            int session =
                _hasScoringInfo
                    ? _scoringInfo.mSession
                    : 0;

            bool isRace =
                session >= 10 &&
                session <= 13;

            if (double.IsNaN(value) ||
                double.IsInfinity(value) ||
                (!isRace && value < 0.0))
            {
                return "";
            }

            if (isRace && lapsBehind > 0)
            {
                return lapsBehind.ToString(
                    CultureInfo.InvariantCulture
                ) + "L";
            }

            double absoluteValue = Math.Abs(value);

            if (isRace && absoluteValue >= 60.0)
            {
                return TimeSpan.FromSeconds(absoluteValue).ToString(
                    @"m\:ss",
                    CultureInfo.InvariantCulture
                );
            }

            return absoluteValue.ToString(
                isRace
                    ? "F1"
                    : "F3",
                CultureInfo.InvariantCulture
            );
        }

        private static string FormatVirtualEnergy(
            double veFraction,
            double fuelFraction)
        {
            double value =
                veFraction > 0.0
                    ? veFraction
                    : fuelFraction;

            if (double.IsNaN(value) ||
                double.IsInfinity(value) ||
                value < 0.0)
            {
                return "";
            }

            return (value * 100.0).ToString(
                "F1",
                CultureInfo.InvariantCulture
            );
        }

        private static string FormatRelativeDriverName(
            string driverName,
            string teamName)
        {
            string normalizedDriverName = (driverName ?? "").Trim();
            if (string.IsNullOrEmpty(normalizedDriverName))
                return "";

            string[] nameParts = normalizedDriverName.Split(
                new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries
            );

            string formattedDriverName = nameParts.Length > 1
                ? nameParts[0].Substring(0, 1).ToUpperInvariant() + ". " +
                  string.Join(" ", nameParts, 1, nameParts.Length - 1).ToUpperInvariant()
                : normalizedDriverName.ToUpperInvariant();

            string normalizedTeamName = (teamName ?? "").Trim();
            return string.IsNullOrEmpty(normalizedTeamName)
                ? formattedDriverName
                : formattedDriverName + " (" + normalizedTeamName.ToUpperInvariant() + ")";
        }

        private static string FormatDriverName(string driverName)
        {
            string normalizedDriverName = (driverName ?? "").Trim();
            if (string.IsNullOrEmpty(normalizedDriverName))
                return "";

            string[] nameParts = normalizedDriverName.Split(
                new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries
            );

            if (nameParts.Length < 2)
                return normalizedDriverName;

            return nameParts[0].Substring(0, 1).ToUpperInvariant() + ". " +
                   string.Join(" ", nameParts, 1, nameParts.Length - 1);
        }

        private static string DecodeString(
            byte[] bytes)
        {
            if (bytes == null)
                return "";

            int length =
                Array.IndexOf(
                    bytes,
                    (byte)0
                );

            if (length < 0)
                length = bytes.Length;

            return Encoding.UTF8.GetString(
                bytes,
                0,
                length
            ).Trim();
        }

        public void End(
            PluginManager pluginManager)
        {
            _standingsApi.Dispose();
            _sharedMemory.Dispose();
        }
    }

    internal sealed class MMOPluginSettingsControl : UserControl
    {
        private const string LatestReleaseApi = "https://api.github.com/repos/St54Kevin/MMOPlugin/releases/latest";
        private readonly PluginManager _pluginManager;
        private readonly List<string> _propertyNames;
        private readonly ObservableCollection<MMOPropertyDisplayItem> _displayedProperties =
            new ObservableCollection<MMOPropertyDisplayItem>();
        private readonly TextBox _searchBox;
        private readonly ListView _propertyList;
        private readonly TextBlock _resultCount;
        private readonly DispatcherTimer _refreshTimer;
        private readonly TextBlock _updateStatus;
        private readonly Button _checkUpdateButton;
        private readonly Button _installUpdateButton;
        private readonly CheckBox _leaderboardTeamNamesOption;
        private readonly CheckBox _additionalRelativeTeamNamesOption;
        private readonly ComboBox _tyreCompoundSymbolOption;
        private readonly Action<bool> _setLeaderboardTeamNamesOption;
        private readonly Action<bool> _setAdditionalRelativeTeamNamesOption;
        private readonly Action<int> _setTyreCompoundSymbolMode;
        private GitHubRelease _latestRelease;
        private bool _checkingUpdate;
        private bool _installingUpdate;

        public MMOPluginSettingsControl(
            PluginManager pluginManager,
            string version,
            List<string> propertyNames,
            bool showTeamNamesInsteadOfDriverNames,
            Action<bool> setLeaderboardTeamNamesOption,
            bool additionalTeamNamesInRelative,
            Action<bool> setAdditionalRelativeTeamNamesOption,
            int tyreCompoundSymbolMode,
            Action<int> setTyreCompoundSymbolMode)
        {
            _pluginManager = pluginManager;
            _propertyNames = propertyNames ?? new List<string>();
            _setLeaderboardTeamNamesOption = setLeaderboardTeamNamesOption;
            _setAdditionalRelativeTeamNamesOption = setAdditionalRelativeTeamNamesOption;
            _setTyreCompoundSymbolMode = setTyreCompoundSymbolMode;
            Background = Brushes.Transparent;
            _searchBox = new TextBox
            {
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(8, 5, 8, 5),
                ToolTip = "Search MMO properties",
                MinWidth = 220,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            GridView propertyGrid = new GridView();
            propertyGrid.Columns.Add(new GridViewColumn
            {
                Header = "Property",
                Width = 380,
                DisplayMemberBinding = new Binding("Name")
            });
            propertyGrid.Columns.Add(new GridViewColumn
            {
                Header = "Current value",
                Width = 190,
                DisplayMemberBinding = new Binding("Value")
            });
            _propertyList = new ListView
            {
                View = propertyGrid,
                ItemsSource = _displayedProperties,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(0),
                FontSize = 12
            };
            VirtualizingPanel.SetIsVirtualizing(_propertyList, true);
            _resultCount = new TextBlock
            {
                Margin = new Thickness(0),
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11
            };

            _updateStatus = new TextBlock
            {
                Text = "Checking GitHub releases…",
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 12, 0),
                FontSize = 12
            };
            _checkUpdateButton = new Button
            {
                Content = "Check",
                Padding = new Thickness(10, 5, 10, 5),
                MinWidth = 72,
                Margin = new Thickness(0, 0, 7, 0)
            };
            _installUpdateButton = new Button
            {
                Content = "Update",
                Padding = new Thickness(10, 5, 10, 5),
                MinWidth = 76,
                Visibility = Visibility.Collapsed,
                IsEnabled = false
            };
            _leaderboardTeamNamesOption = new CheckBox
            {
                Content = "Show Team Names in Leaderboard",
                IsChecked = showTeamNamesInsteadOfDriverNames,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 6),
                VerticalAlignment = VerticalAlignment.Top
            };
            _additionalRelativeTeamNamesOption = new CheckBox
            {
                Content = "Additional Team Names in Relative",
                IsChecked = additionalTeamNamesInRelative,
                FontSize = 13,
                Margin = new Thickness(0, 12, 0, 6),
                VerticalAlignment = VerticalAlignment.Top
            };
            _tyreCompoundSymbolOption = new ComboBox
            {
                Width = 260,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _tyreCompoundSymbolOption.Items.Add("Display for all cars");
            _tyreCompoundSymbolOption.Items.Add("Only own class");
            _tyreCompoundSymbolOption.Items.Add("Own class, only when different");
            _tyreCompoundSymbolOption.SelectedIndex =
                tyreCompoundSymbolMode >= 0 && tyreCompoundSymbolMode <= 2
                    ? tyreCompoundSymbolMode
                    : 0;

            Grid root = new Grid
            {
                Margin = new Thickness(12)
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Border header = new Border
            {
                Padding = new Thickness(0, 0, 0, 10),
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 75, 83)),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };

            System.Windows.Documents.Hyperlink downloadLink =
                new System.Windows.Documents.Hyperlink(
                    new System.Windows.Documents.Run("Overlay")
                )
                {
                    NavigateUri = new Uri(
                        "https://www.overtake.gg/downloads/lmu-simhub-mmo-modern-multiclass-overlay-standings-relative-tires.74112/"
                    )
                };
            downloadLink.RequestNavigate += DownloadLink_RequestNavigate;

            System.Windows.Documents.Hyperlink releasesLink =
                new System.Windows.Documents.Hyperlink(
                    new System.Windows.Documents.Run("GitHub")
                )
                {
                    NavigateUri = new Uri("https://github.com/St54Kevin/MMOPlugin/releases")
                };
            releasesLink.RequestNavigate += DownloadLink_RequestNavigate;

            DockPanel headerLayout = new DockPanel { LastChildFill = true };
            TextBlock currentVersion = new TextBlock
            {
                Text = "v" + (version ?? ""),
                Opacity = 0.72,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            TextBlock headerLinks = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            headerLinks.Inlines.Add(downloadLink);
            headerLinks.Inlines.Add(new System.Windows.Documents.Run("  ·  "));
            headerLinks.Inlines.Add(releasesLink);
            StackPanel headerActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            headerActions.Children.Add(headerLinks);
            headerActions.Children.Add(currentVersion);
            DockPanel.SetDock(headerActions, Dock.Right);
            headerLayout.Children.Add(headerActions);
            StackPanel titleGroup = new StackPanel { Orientation = Orientation.Horizontal };
            Image pluginLogo = new Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri(
                        "pack://application:,,,/MMOPlugin;component/rsz_logo_transp.png",
                        UriKind.Absolute
                    )
                ),
                Width = 38,
                Height = 38,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                ToolTip = "MMO Plugin by Kevin_Karas"
            };
            titleGroup.Children.Add(pluginLogo);
            titleGroup.Children.Add(new TextBlock
            {
                Text = "MMO Plugin",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            titleGroup.Children.Add(new TextBlock
            {
                Text = "by Kevin_Karas",
                Opacity = 0.65,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });
            headerLayout.Children.Add(titleGroup);
            header.Child = headerLayout;
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            Border updateCard = new Border
            {
                Margin = new Thickness(0, 9, 0, 10),
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 75, 83)),
                Background = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128))
            };
            DockPanel updateLayout = new DockPanel { LastChildFill = true };
            StackPanel updateButtons = new StackPanel { Orientation = Orientation.Horizontal };
            updateButtons.Children.Add(_checkUpdateButton);
            updateButtons.Children.Add(_installUpdateButton);
            DockPanel.SetDock(updateButtons, Dock.Right);
            updateLayout.Children.Add(updateButtons);
            updateLayout.Children.Add(_updateStatus);
            updateCard.Child = updateLayout;
            Grid.SetRow(updateCard, 1);
            root.Children.Add(updateCard);

            StackPanel propertyHeading = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 3)
            };
            propertyHeading.Children.Add(new TextBlock
            {
                Text = "Properties",
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            propertyHeading.Children.Add(_resultCount);
            Grid propertyArea = new Grid();
            propertyArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            propertyArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            propertyArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(propertyHeading, 0);
            Grid.SetRow(_searchBox, 1);
            Grid.SetRow(_propertyList, 2);
            propertyArea.Children.Add(propertyHeading);
            propertyArea.Children.Add(_searchBox);
            propertyArea.Children.Add(_propertyList);
            Grid.SetRow(propertyArea, 2);
            Grid.SetRowSpan(propertyArea, 2);
            root.Children.Add(propertyArea);

            _searchBox.TextChanged += SearchBox_TextChanged;
            _checkUpdateButton.Click += CheckUpdateButton_Click;
            _installUpdateButton.Click += InstallUpdateButton_Click;
            _leaderboardTeamNamesOption.Checked += TeamNameOption_Changed;
            _leaderboardTeamNamesOption.Unchecked += TeamNameOption_Changed;
            _additionalRelativeTeamNamesOption.Checked += TeamNameOption_Changed;
            _additionalRelativeTeamNamesOption.Unchecked += TeamNameOption_Changed;
            _tyreCompoundSymbolOption.SelectionChanged += TyreCompoundSymbolOption_SelectionChanged;
            Loaded += SettingsControl_Loaded;
            Unloaded += SettingsControl_Unloaded;

            _refreshTimer = new DispatcherTimer(
                DispatcherPriority.Background
            )
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _refreshTimer.Tick += RefreshTimer_Tick;

            StackPanel leaderboardOptions = new StackPanel();
            leaderboardOptions.Children.Add(_leaderboardTeamNamesOption);
            leaderboardOptions.Children.Add(new TextBlock
            {
                Text = "Replaces the DriverName properties with the corresponding TeamName.",
                Opacity = 0.7,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
                Margin = new Thickness(22, 0, 0, 0)
            });

            StackPanel relativeOptions = new StackPanel();
            relativeOptions.Children.Add(_additionalRelativeTeamNamesOption);
            relativeOptions.Children.Add(new TextBlock
            {
                Text = "Adds the team name to DriveNameRelative, formatted as initial, surname, and team.",
                Opacity = 0.7,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
                Margin = new Thickness(22, 0, 0, 0)
            });

            StackPanel generalOptions = new StackPanel();
            generalOptions.Children.Add(new TextBlock
            {
                Text = "Tyre Compound Symbol:",
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 6)
            });
            generalOptions.Children.Add(_tyreCompoundSymbolOption);

            StackPanel settingsPage = new StackPanel
            {
                Margin = new Thickness(16),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            settingsPage.Children.Add(new GroupBox
            {
                Header = "Leaderboard",
                Content = leaderboardOptions,
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(10)
            });
            settingsPage.Children.Add(new GroupBox
            {
                Header = "Relative",
                Content = relativeOptions,
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(10)
            });
            settingsPage.Children.Add(new GroupBox
            {
                Header = "General",
                Content = generalOptions,
                Padding = new Thickness(10)
            });

            TabControl tabs = new TabControl();
            tabs.Items.Add(new TabItem
            {
                Header = "Properties",
                Content = root
            });
            tabs.Items.Add(new TabItem
            {
                Header = "Settings",
                Content = settingsPage
            });

            Content = tabs;
            ApplyPropertyFilter();
        }

        private void TeamNameOption_Changed(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(sender, _leaderboardTeamNamesOption))
            {
                if (_setLeaderboardTeamNamesOption != null)
                    _setLeaderboardTeamNamesOption(_leaderboardTeamNamesOption.IsChecked == true);
            }
            else if (ReferenceEquals(sender, _additionalRelativeTeamNamesOption))
            {
                if (_setAdditionalRelativeTeamNamesOption != null)
                    _setAdditionalRelativeTeamNamesOption(_additionalRelativeTeamNamesOption.IsChecked == true);
            }
        }

        private void TyreCompoundSymbolOption_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (_setTyreCompoundSymbolMode != null &&
                _tyreCompoundSymbolOption.SelectedIndex >= 0)
                _setTyreCompoundSymbolMode(_tyreCompoundSymbolOption.SelectedIndex);
        }

        private void SearchBox_TextChanged(
            object sender,
            TextChangedEventArgs e)
        {
            ApplyPropertyFilter();
        }

        private void ApplyPropertyFilter()
        {
            string query = (_searchBox.Text ?? "").Trim();
            _displayedProperties.Clear();

            foreach (string name in _propertyNames)
            {
                string fullName = "MMOPlugin." + name;

                if (!string.IsNullOrEmpty(query) &&
                    fullName.IndexOf(
                        query,
                        StringComparison.OrdinalIgnoreCase
                    ) < 0)
                {
                    continue;
                }

                _displayedProperties.Add(
                    new MMOPropertyDisplayItem
                    {
                        Name = fullName,
                        Value = GetCurrentValue(name)
                    }
                );
            }

            _resultCount.Text =
                _displayedProperties.Count.ToString(CultureInfo.InvariantCulture) +
                " properties";
        }

        private void RefreshCurrentValues()
        {
            foreach (MMOPropertyDisplayItem item in _displayedProperties)
            {
                string propertyName = item.Name.Substring(
                    "MMOPlugin.".Length
                );
                item.Value = GetCurrentValue(propertyName);
            }
        }

        private string GetCurrentValue(string propertyName)
        {
            if (_pluginManager == null)
                return "";

            try
            {
                object value = _pluginManager.GetPropertyValue(
                    "MMOPlugin." + propertyName
                );

                if (value == null)
                    value = _pluginManager.GetPropertyValue(propertyName);

                if (value == null)
                    return "";

                IFormattable formattable = value as IFormattable;
                return formattable != null
                    ? formattable.ToString(null, CultureInfo.InvariantCulture)
                    : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return "";
            }
        }

        private void SettingsControl_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            RefreshCurrentValues();
            _refreshTimer.Start();
            if (!_checkingUpdate && _latestRelease == null)
                CheckForUpdatesAsync();
        }

        private void SettingsControl_Unloaded(
            object sender,
            RoutedEventArgs e)
        {
            _refreshTimer.Stop();
        }

        private void RefreshTimer_Tick(
            object sender,
            EventArgs e)
        {
            RefreshCurrentValues();
        }

        private static void DownloadLink_RequestNavigate(
            object sender,
            RequestNavigateEventArgs e)
        {
            Process.Start(e.Uri.AbsoluteUri);
            e.Handled = true;
        }

        private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            await CheckForUpdatesAsync();
        }

        private async Task CheckForUpdatesAsync()
        {
            if (_checkingUpdate)
                return;
            _checkingUpdate = true;
            _checkUpdateButton.IsEnabled = false;
            _updateStatus.Text = "Checking GitHub releases…";
            _updateStatus.Foreground = SystemColors.ControlTextBrush;
            _installUpdateButton.Visibility = Visibility.Collapsed;
            try
            {
                GitHubRelease release = await Task.Run(LoadLatestRelease);
                Version available;
                Version installed;
                if (release == null ||
                    (!TryParseVersion(release.TagName, out available) &&
                     !TryParseVersion(release.Name, out available)))
                {
                    _updateStatus.Text = "No usable release found. Open GitHub releases.";
                    _latestRelease = null;
                    return;
                }
                _latestRelease = release;
                Version.TryParse(MMOPlugin.PluginVersion, out installed);
                if (installed == null || available > installed)
                {
                    _updateStatus.Foreground = Brushes.OrangeRed;
                    _updateStatus.Text = "Update available · v" + available.ToString();
                    _installUpdateButton.Visibility = Visibility.Visible;
                    _installUpdateButton.IsEnabled = FindPluginAsset(release) != null;
                    if (!_installUpdateButton.IsEnabled)
                        _updateStatus.Text += " · release has no MMOPlugin.dll/.zip asset";
                }
                else
                {
                    _updateStatus.Text = "Already up to date · v" + MMOPlugin.PluginVersion;
                    _updateStatus.Foreground = Brushes.LightGreen;
                    _latestRelease = null;
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound)
                {
                    response.Close();
                    _updateStatus.Text = "No public GitHub release found yet. See GitHub releases.";
                    _updateStatus.Foreground = SystemColors.ControlTextBrush;
                    _latestRelease = null;
                }
                else
                {
                    _updateStatus.Text = "Could not check for updates: " + ex.Message;
                    _updateStatus.Foreground = SystemColors.ControlTextBrush;
                    _latestRelease = null;
                }
            }
            catch (Exception ex)
            {
                _updateStatus.Text = "Could not check for updates: " + ex.Message;
                _updateStatus.Foreground = SystemColors.ControlTextBrush;
                _latestRelease = null;
            }
            finally
            {
                _checkingUpdate = false;
                _checkUpdateButton.IsEnabled = true;
            }
        }

        private static GitHubRelease LoadLatestRelease()
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(LatestReleaseApi);
            request.UserAgent = "MMOPlugin-Updater/" + MMOPlugin.PluginVersion;
            request.Accept = "application/vnd.github+json";
            request.Timeout = 15000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(GitHubRelease));
                return (GitHubRelease)serializer.ReadObject(stream);
            }
        }

        private static bool TryParseVersion(string text, out Version version)
        {
            string normalized = (text ?? "").Trim();
            while (normalized.Length > 0 && (normalized[0] == 'v' || normalized[0] == 'V'))
                normalized = normalized.Substring(1);
            int suffix = normalized.IndexOfAny(new[] { '-', '+' });
            if (suffix >= 0)
                normalized = normalized.Substring(0, suffix);
            if (Version.TryParse(normalized, out version))
                return true;

            Match match = Regex.Match(
                text ?? "",
                @"(?<!\d)v?(\d+\.\d+(?:\.\d+){0,2})",
                RegexOptions.IgnoreCase
            );
            return match.Success && Version.TryParse(match.Groups[1].Value, out version);
        }

        private static GitHubReleaseAsset FindPluginAsset(GitHubRelease release)
        {
            if (release == null || release.Assets == null)
                return null;
            foreach (GitHubReleaseAsset asset in release.Assets)
            {
                string name = asset.Name ?? "";
                if (string.Equals(name, "MMOPlugin.dll", StringComparison.OrdinalIgnoreCase) ||
                    (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                     name.IndexOf("MMOPlugin", StringComparison.OrdinalIgnoreCase) >= 0))
                    return asset;
            }
            return null;
        }

        private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_installingUpdate || _latestRelease == null)
                return;
            GitHubReleaseAsset asset = FindPluginAsset(_latestRelease);
            if (asset == null)
                return;
            _installingUpdate = true;
            _installUpdateButton.IsEnabled = false;
            _checkUpdateButton.IsEnabled = false;
            _updateStatus.Text = "Downloading update…";
            try
            {
                string target = typeof(MMOPlugin).Assembly.Location;
                string tempDirectory = Path.Combine(Path.GetTempPath(), "MMOPluginUpdate");
                Directory.CreateDirectory(tempDirectory);
                string downloaded = Path.Combine(tempDirectory, Path.GetFileName(asset.Name));
                using (WebClient client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "MMOPlugin-Updater/" + MMOPlugin.PluginVersion;
                    await client.DownloadFileTaskAsync(new Uri(asset.DownloadUrl), downloaded);
                }
                string stagedDll = downloaded;
                if (downloaded.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    string extractDirectory = Path.Combine(tempDirectory, "package");
                    if (Directory.Exists(extractDirectory))
                        Directory.Delete(extractDirectory, true);
                    ZipFile.ExtractToDirectory(downloaded, extractDirectory);
                    string[] dlls = Directory.GetFiles(extractDirectory, "MMOPlugin.dll", SearchOption.AllDirectories);
                    if (dlls.Length == 0)
                        throw new InvalidDataException("The release archive does not contain MMOPlugin.dll.");
                    stagedDll = dlls[0];
                }
                if (!File.Exists(stagedDll) || !string.Equals(Path.GetFileName(stagedDll), "MMOPlugin.dll", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded release is not an MMOPlugin.dll or supported ZIP package.");

                MessageBoxResult confirmation = ShowUpdateRestartConfirmation();
                if (confirmation != MessageBoxResult.Yes)
                {
                    _updateStatus.Text = "Update downloaded. SimHub will restart only after you confirm.";
                    _installingUpdate = false;
                    _installUpdateButton.IsEnabled = true;
                    _checkUpdateButton.IsEnabled = true;
                    return;
                }

                StartUpdater(stagedDll, target);
                _updateStatus.Text = "Update downloaded. SimHub will restart to install it.";
                _pluginManager.RequestApplicationExit(false);
            }
            catch (Exception ex)
            {
                _updateStatus.Text = "Update failed: " + ex.Message;
                _installingUpdate = false;
                _installUpdateButton.IsEnabled = true;
                _checkUpdateButton.IsEnabled = true;
            }
        }

        private MessageBoxResult ShowUpdateRestartConfirmation()
        {
            const string message =
                "The update has been downloaded. SimHub must close and restart to install it. Continue?";
            Window owner = Window.GetWindow(this);
            return owner != null
                ? MessageBox.Show(
                    owner,
                    message,
                    "MMO Plugin update",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information
                )
                : MessageBox.Show(
                    message,
                    "MMO Plugin update",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information
                );
        }

        private void StartUpdater(string stagedDll, string targetDll)
        {
            string executable = Process.GetCurrentProcess().MainModule.FileName;
            string script = Path.Combine(Path.GetTempPath(), "MMOPluginUpdate", "install-update.ps1");
            string scriptText =
                "$ErrorActionPreference = 'Stop'\r\n" +
                "$processId = [int]$args[0]\r\n$staged = $args[1]\r\n$target = $args[2]\r\n$app = $args[3]\r\n" +
                "try { Wait-Process -Id $processId -Timeout 120 } catch {}\r\n" +
                "Start-Sleep -Milliseconds 700\r\n" +
                "$backup = $target + '.bak'\r\nif (Test-Path $backup) { Remove-Item -LiteralPath $backup -Force }\r\n" +
                "Copy-Item -LiteralPath $target -Destination $backup -Force\r\n" +
                "Copy-Item -LiteralPath $staged -Destination $target -Force\r\n" +
                "$work = Split-Path -Parent $app\r\n" +
                "Start-Process -FilePath $app -WorkingDirectory $work\r\n";
            File.WriteAllText(script, scriptText, new UTF8Encoding(false));
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell\\v1.0\\powershell.exe"),
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " + QuoteArgument(script) +
                    " " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) +
                    " " + QuoteArgument(stagedDll) + " " + QuoteArgument(targetDll) + " " + QuoteArgument(executable),
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(start);
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }
    }

    [DataContract]
    internal sealed class GitHubRelease
    {
        [DataMember(Name = "tag_name")]
        public string TagName { get; set; }
        [DataMember(Name = "name")]
        public string Name { get; set; }
        [DataMember(Name = "assets")]
        public GitHubReleaseAsset[] Assets { get; set; }
    }

    [DataContract]
    internal sealed class GitHubReleaseAsset
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }
        [DataMember(Name = "browser_download_url")]
        public string DownloadUrl { get; set; }
    }

    internal sealed class MMOPropertyDisplayItem : INotifyPropertyChanged
    {
        private string _value;

        public string Name { get; set; }

        public string Value
        {
            get { return _value; }
            set
            {
                if (_value == value)
                    return;

                _value = value;

                PropertyChangedEventHandler handler = PropertyChanged;
                if (handler != null)
                    handler(this, new PropertyChangedEventArgs("Value"));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
