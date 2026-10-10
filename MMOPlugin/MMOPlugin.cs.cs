using System;
using System.Globalization;
using System.Text;
using GameReaderCommon;
using SimHub.Plugins;

namespace MMOPlugin
{
    [PluginName("MMO Plugin")]
    [PluginAuthor("Kevin")]
    [PluginDescription("Extended Le Mans Ultimate standings data.")]
    public class MMOPlugin : IPlugin, IDataPlugin
    {
        public PluginManager PluginManager { get; set; }

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

        public void Init(PluginManager pluginManager)
        {
            PluginManager = pluginManager;

            _standingsApi.Start();

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

            for (int position = 1;
                 position <= MaxVehicles;
                 position++)
            {
                int capturedPosition = position;

                string positionName =
                    capturedPosition.ToString("00");

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

                        return vehicle != null
                            ? vehicle.DriverName ?? ""
                            : "";
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
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null
                            ? vehicle.LastLapTime
                            : 0.0;
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

                        return FormatGap(
                            vehicle.GapToLeader
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

                        return FormatGap(
                            vehicle.IntervalToNext
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
                        "FinishStatus",
                    valueProvider: () =>
                    {
                        LMUApiVehicle vehicle =
                            _standingsApi.GetVehicleByPosition(
                                capturedPosition
                            );

                        return vehicle != null
                            ? vehicle.FinishStatus ?? ""
                            : "";
                    }
                );
            }
        }

        public void DataUpdate(
            PluginManager pluginManager,
            ref GameData data)
        {
            _sharedMemory.UpdateConnection();

            if (!_sharedMemory.IsConnected)
                return;

            _hasScoringInfo =
                _sharedMemory.TryReadScoringInfo(
                    out _scoringInfo
                );

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
        }

        private string FormatGap(
            double value)
        {
            if (double.IsNaN(value) ||
                double.IsInfinity(value) ||
                value < 0.0)
            {
                return "";
            }

            int session =
                _hasScoringInfo
                    ? _scoringInfo.mSession
                    : 0;

            bool isRace =
                session >= 10 &&
                session <= 13;

            return value.ToString(
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
}