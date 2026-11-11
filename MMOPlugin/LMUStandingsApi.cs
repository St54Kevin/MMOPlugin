using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Web.Script.Serialization;
using System.Threading;

namespace MMOPlugin
{
    public sealed class LMUStandingsApi : IDisposable
    {
        private const string StandingsUrl =
            "http://127.0.0.1:6397/rest/watch/standings";
        private const string RepairAndRefuelUrl =
            "http://127.0.0.1:6397/rest/garage/UIScreen/RepairAndRefuel";

        private const int MaxVehicles = 62;

        private readonly object _lock =
            new object();

        private Timer _timer;

        private LMUApiVehicle[] _vehicles =
            new LMUApiVehicle[MaxVehicles];
        private readonly DateTime[] _vehicleLastUpdatedUtc =
            new DateTime[MaxVehicles];
        private static readonly TimeSpan VehicleSnapshotGracePeriod =
            TimeSpan.FromSeconds(1.5);

        private bool _isConnected;

        private string _lastError = "";

        private int _session = int.MinValue;
        private DateTime _lastFuelInfoRequestUtc = DateTime.MinValue;
        private double _currentFuelLiters;
        private double _fuelCapacityLiters;
        private bool _hasFuelInfo;
        private Timer _vehicleMetadataTimer;
        private int _updateRunning;
        private int _metadataUpdateRunning;
        private int _metadataRefreshPending;
        private int _vehicleMetadataVersion;
        private readonly Dictionary<string, LMUVehicleModelMetadata> _vehicleMetadata =
            new Dictionary<string, LMUVehicleModelMetadata>(StringComparer.OrdinalIgnoreCase);
        public event Action VehicleMetadataUpdated;

        public int VehicleMetadataVersion
        {
            get { return Volatile.Read(ref _vehicleMetadataVersion); }
        }

        public bool TryGetFuelInfo(out double currentFuelLiters, out double capacityLiters)
        {
            lock (_lock)
            {
                currentFuelLiters = _currentFuelLiters;
                capacityLiters = _fuelCapacityLiters;
                return _hasFuelInfo;
            }
        }

        public bool IsConnected
        {
            get
            {
                lock (_lock)
                {
                    return _isConnected;
                }
            }
        }

        public string LastError
        {
            get
            {
                lock (_lock)
                {
                    return _lastError;
                }
            }
        }

        public void Start()
        {
            if (_timer != null)
                return;

            _timer = new Timer(
                Update,
                null,
                0,
                250
            );
            // Load metadata once after startup; later refreshes are requested
            // when the active LMU session changes instead of every 30 seconds.
            _vehicleMetadataTimer = new Timer(
                UpdateVehicleMetadata,
                null,
                Timeout.Infinite,
                Timeout.Infinite
            );
        }

        public bool TryGetVehicleMetadata(
            string vehicleFile,
            string vehicleName,
            string teamName,
            string carNumber,
            out string manufacturer,
            out string model)
        {
            lock (_lock)
            {
                LMUVehicleModelMetadata metadata;
                if (TryFindVehicleMetadata(vehicleFile, out metadata) ||
                    TryFindVehicleMetadata(vehicleName, out metadata) ||
                    TryFindVehicleMetadata(teamName + " " + carNumber, out metadata) ||
                    TryFindVehicleMetadata(teamName, out metadata))
                {
                    manufacturer = metadata.Manufacturer;
                    model = metadata.Model;
                    return true;
                }
            }
            manufacturer = "";
            model = "";
            return false;
        }

        private bool TryFindVehicleMetadata(string identity, out LMUVehicleModelMetadata metadata)
        {
            string key = NormalizeVehicleIdentity(identity);
            if (!string.IsNullOrEmpty(key) && _vehicleMetadata.TryGetValue(key, out metadata))
                return true;
            metadata = null;
            return false;
        }

        private void UpdateVehicleMetadata(object state)
        {
            if (Interlocked.Exchange(ref _metadataUpdateRunning, 1) != 0)
            {
                Interlocked.Exchange(ref _metadataRefreshPending, 1);
                return;
            }

            bool measurePerformance = PluginPerformanceDiagnostics.Enabled;
            long performanceStartedAt = measurePerformance
                ? System.Diagnostics.Stopwatch.GetTimestamp()
                : 0L;
            long allocatedBytesAtStart = measurePerformance
                ? GC.GetAllocatedBytesForCurrentThread()
                : 0L;
            try
            {
                Interlocked.Exchange(ref _metadataRefreshPending, 0);
                UpdateVehicleMetadataCore();
            }
            finally
            {
                if (measurePerformance)
                    PluginPerformanceDiagnostics.VehicleMetadataPoll.Add(performanceStartedAt, allocatedBytesAtStart);
                Volatile.Write(ref _metadataUpdateRunning, 0);
                if (Interlocked.Exchange(ref _metadataRefreshPending, 0) != 0)
                    RequestVehicleMetadataRefresh();
            }
        }

        private void UpdateVehicleMetadataCore()
        {
            string[] endpoints =
            {
                "http://127.0.0.1:6397/rest/race/car",
                "http://127.0.0.1:6397/rest/sessions/getAllVehicles"
            };
            foreach (string endpoint in endpoints)
            {
                try
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
                    request.Timeout = 2500;
                    request.ReadWriteTimeout = 2500;
                    request.UserAgent = "MMOPlugin";
                    string json;
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (Stream stream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                        json = reader.ReadToEnd();

                    Dictionary<string, LMUVehicleModelMetadata> parsed = ParseVehicleMetadata(json);
                    if (parsed.Count == 0) continue;
                    bool changed = false;
                    lock (_lock)
                    {
                        foreach (KeyValuePair<string, LMUVehicleModelMetadata> item in parsed)
                        {
                            LMUVehicleModelMetadata previous;
                            if (!_vehicleMetadata.TryGetValue(item.Key, out previous) ||
                                !string.Equals(previous.Manufacturer, item.Value.Manufacturer, StringComparison.OrdinalIgnoreCase) ||
                                !string.Equals(previous.Model, item.Value.Model, StringComparison.OrdinalIgnoreCase))
                                changed = true;
                            _vehicleMetadata[item.Key] = item.Value;
                        }
                    }
                    if (changed)
                        Interlocked.Increment(ref _vehicleMetadataVersion);
                    if (changed && VehicleMetadataUpdated != null)
                        VehicleMetadataUpdated();
                }
                catch
                {
                    // Vehicle metadata is optional and will be retried later.
                }
            }
        }

        private static Dictionary<string, LMUVehicleModelMetadata> ParseVehicleMetadata(string json)
        {
            Dictionary<string, LMUVehicleModelMetadata> results =
                new Dictionary<string, LMUVehicleModelMetadata>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(json)) return results;
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer
                {
                    MaxJsonLength = 5 * 1024 * 1024
                };
                CollectVehicleMetadata(serializer.DeserializeObject(json), results);
            }
            catch
            {
                // Payloads vary between LMU versions; ignore unrecognized shapes.
            }
            return results;
        }

        private static void CollectVehicleMetadata(
            object node,
            Dictionary<string, LMUVehicleModelMetadata> results)
        {
            IDictionary<string, object> dictionary = node as IDictionary<string, object>;
            if (dictionary != null)
            {
                string manufacturer = FindStringField(dictionary,
                    "manufacturer", "vehicleManufacturer", "carManufacturer",
                    "manufacturerName", "brand", "brandName", "vehicleBrand",
                    "vehicleBrandName", "carBrand", "make", "makeName", "vehicleMake");
                string model = FindStringField(dictionary,
                    "model", "vehicleModel", "vehicle_model", "carModel", "car_model",
                    "modelName", "model_name", "vehicleModelName", "vehicleName",
                    "carName", "fullName", "displayName", "name");
                if (!string.IsNullOrWhiteSpace(manufacturer) &&
                    !string.IsNullOrWhiteSpace(model))
                {
                    LMUVehicleModelMetadata metadata = new LMUVehicleModelMetadata
                    {
                        Manufacturer = manufacturer.Trim(),
                        Model = model.Trim()
                    };
                    foreach (KeyValuePair<string, object> field in dictionary)
                    {
                        AddMetadataIdentity(results, field.Key, metadata);
                        AddMetadataIdentity(results, field.Value as string, metadata);
                    }
                    string teamName = FindStringField(dictionary,
                        "teamName", "fullTeamName", "fullTeam", "team", "entryName");
                    string carNumber = FindStringField(dictionary,
                        "carNumber", "vehicleNumber", "number", "raceNumber");
                    AddMetadataIdentity(results, teamName + " " + carNumber, metadata);
                }
                foreach (object value in dictionary.Values)
                    CollectVehicleMetadata(value, results);
                return;
            }

            object[] array = node as object[];
            if (array != null)
                foreach (object value in array)
                    CollectVehicleMetadata(value, results);
        }

        private static string FindStringField(
            IDictionary<string, object> dictionary,
            params string[] aliases)
        {
            foreach (string alias in aliases)
                foreach (KeyValuePair<string, object> field in dictionary)
                    if (string.Equals(field.Key, alias, StringComparison.OrdinalIgnoreCase))
                    {
                        string value = field.Value as string;
                        if (!string.IsNullOrWhiteSpace(value)) return value;
                    }
            return "";
        }

        private static void AddMetadataIdentity(
            Dictionary<string, LMUVehicleModelMetadata> results,
            string identity,
            LMUVehicleModelMetadata metadata)
        {
            string key = NormalizeVehicleIdentity(identity);
            if (!string.IsNullOrEmpty(key) && !results.ContainsKey(key))
                results.Add(key, metadata);
        }

        private static string NormalizeVehicleIdentity(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity)) return "";
            StringBuilder normalized = new StringBuilder(identity.Length);
            foreach (char character in identity.Trim())
                if (char.IsLetterOrDigit(character))
                    normalized.Append(char.ToLowerInvariant(character));
            return normalized.ToString();
        }

        public void SetSession(int session)
        {
            bool changed = false;
            lock (_lock)
            {
                if (_session != session)
                {
                    Array.Clear(_vehicleLastUpdatedUtc, 0, _vehicleLastUpdatedUtc.Length);
                    _session = session;
                    changed = true;
                }
            }

            if (changed)
                RequestVehicleMetadataRefresh();
        }

        private void RequestVehicleMetadataRefresh()
        {
            Timer timer = _vehicleMetadataTimer;
            if (timer == null) return;
            Interlocked.Exchange(ref _metadataRefreshPending, 1);
            try { timer.Change(1000, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }

        public LMUApiVehicle GetVehicleByPosition(
            int position)
        {
            if (position < 1 ||
                position > MaxVehicles)
            {
                return null;
            }

            LMUApiVehicle[] vehicles = Volatile.Read(ref _vehicles);
            return vehicles[position - 1];
        }

        private static LMUApiVehicle CopyVehicle(LMUApiVehicle source)
        {
            if (source == null) return null;
            LMUApiVehicle copy = new LMUApiVehicle
            {
                Position = source.Position,
                CarNumber = source.CarNumber,
                CarClass = source.CarClass,
                DriverName = source.DriverName,
                TeamName = source.TeamName,
                BestLapTime = source.BestLapTime,
                LastLapTime = source.LastLapTime,
                TimeBehindLeader = source.TimeBehindLeader,
                TimeBehindNext = source.TimeBehindNext,
                TimeBehindClassLeader = source.TimeBehindClassLeader,
                PitState = source.PitState,
                Pitting = source.Pitting,
                InGarageStall = source.InGarageStall,
                FuelFraction = source.FuelFraction,
                Penalties = source.Penalties,
                FinishStatus = source.FinishStatus,
                PositionClass = source.PositionClass,
                PracticeQualifyingGap = source.PracticeQualifyingGap,
                PracticeQualifyingInterval = source.PracticeQualifyingInterval
            };
            if (source.HasVeFraction)
                copy.VeFraction = source.VeFraction;
            return copy;
        }

        private void Update(object state)
        {
            if (Interlocked.Exchange(ref _updateRunning, 1) != 0)
            {
                PluginPerformanceDiagnostics.RecordSkippedStandingsApiPoll();
                return;
            }

            bool measurePerformance = PluginPerformanceDiagnostics.Enabled;
            long performanceStartedAt = measurePerformance
                ? System.Diagnostics.Stopwatch.GetTimestamp()
                : 0L;
            long allocatedBytesAtStart = measurePerformance
                ? GC.GetAllocatedBytesForCurrentThread()
                : 0L;
            try
            {
                string json;

                using (WebClient client = new TimeoutWebClient(1500))
                {
                    client.Encoding = Encoding.UTF8;

                    json = client.DownloadString(
                        StandingsUrl
                    );
                }

                DataContractJsonSerializer serializer =
                    new DataContractJsonSerializer(
                        typeof(LMUApiVehicle[])
                    );

                using (MemoryStream stream =
                    new MemoryStream(
                        Encoding.UTF8.GetBytes(json)))
                {
                    LMUApiVehicle[] vehicles =
                        (LMUApiVehicle[])serializer.ReadObject(
                            stream
                        );

                    UpdateVehicles(vehicles);
                }

                if ((DateTime.UtcNow - _lastFuelInfoRequestUtc).TotalSeconds >= 1.0)
                {
                    _lastFuelInfoRequestUtc = DateTime.UtcNow;
                    UpdateFuelInfo();
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _isConnected = false;

                    _lastError =
                        ex.GetType().Name +
                        ": " +
                        ex.Message;
                }
            }
            finally
            {
                if (measurePerformance)
                    PluginPerformanceDiagnostics.StandingsApiPoll.Add(performanceStartedAt, allocatedBytesAtStart);
                Volatile.Write(ref _updateRunning, 0);
            }
        }

        private void UpdateFuelInfo()
        {
            try
            {
                string json;
                using (WebClient client = new TimeoutWebClient(1500))
                {
                    client.Encoding = Encoding.UTF8;
                    json = client.DownloadString(RepairAndRefuelUrl);
                }

                DataContractJsonSerializer serializer =
                    new DataContractJsonSerializer(typeof(LMURepairAndRefuelResponse));
                LMURepairAndRefuelResponse response;
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    response = (LMURepairAndRefuelResponse)serializer.ReadObject(stream);
                }

                LMURepairFuelInfo fuelInfo = response != null ? response.FuelInfo : null;
                bool valid = fuelInfo != null &&
                    !double.IsNaN(fuelInfo.CurrentFuel) &&
                    !double.IsInfinity(fuelInfo.CurrentFuel) &&
                    !double.IsNaN(fuelInfo.MaxFuel) &&
                    !double.IsInfinity(fuelInfo.MaxFuel) &&
                    fuelInfo.CurrentFuel >= 0.0 && fuelInfo.MaxFuel > 0.0;

                lock (_lock)
                {
                    _hasFuelInfo = valid;
                    if (valid)
                    {
                        _currentFuelLiters = fuelInfo.CurrentFuel;
                        _fuelCapacityLiters = fuelInfo.MaxFuel;
                    }
                }
            }
            catch
            {
                lock (_lock)
                    _hasFuelInfo = false;
            }
        }

        private void UpdateVehicles(
            LMUApiVehicle[] source)
        {
            LMUApiVehicle[] newVehicles =
                new LMUApiVehicle[MaxVehicles];

            if (source != null)
            {
                foreach (LMUApiVehicle vehicle in source)
                {
                    if (vehicle == null)
                        continue;

                    int position =
                        vehicle.Position;

                    if (position < 1 ||
                        position > MaxVehicles)
                    {
                        continue;
                    }

                    newVehicles[position - 1] =
                        vehicle;
                }
            }

            int session;
            DateTime now = DateTime.UtcNow;

            lock (_lock)
            {
                session = _session;

                // LMU can briefly omit a row while rebuilding its standings
                // response. Keep the last row for that position only for a
                // short grace period, then let it disappear normally.
                for (int i = 0; i < newVehicles.Length; i++)
                {
                    if (newVehicles[i] != null)
                    {
                        LMUApiVehicle previous = _vehicles[i];
                        if (previous != null && AreSameVehicle(previous, newVehicles[i]))
                            PreserveMissingVehicleFields(newVehicles[i], previous);
                        _vehicleLastUpdatedUtc[i] = now;
                        continue;
                    }

                    DateTime lastUpdated = _vehicleLastUpdatedUtc[i];
                    if (_vehicles[i] != null && lastUpdated != DateTime.MinValue &&
                        now - lastUpdated <= VehicleSnapshotGracePeriod)
                    {
                        // Calculations below update fields on each vehicle.
                        // Copy grace-period rows so the currently published
                        // snapshot stays immutable for lock-free readers.
                        newVehicles[i] = CopyVehicle(_vehicles[i]);
                    }
                    else
                    {
                        _vehicleLastUpdatedUtc[i] = DateTime.MinValue;
                    }
                }
            }

            bool isPracticeOrQualifying =
                IsPracticeOrQualifying(session);

            if (isPracticeOrQualifying)
            {
                CalculatePracticeQualifyingData(
                    newVehicles
                );
            }
            else
            {
                // The practice/qualifying gap fields default to zero when they
                // are not calculated. Mark them unavailable so race getters
                // use the race timing values from LMU instead.
                for (int i = 0; i < newVehicles.Length; i++)
                {
                    if (newVehicles[i] == null)
                        continue;

                    newVehicles[i].PracticeQualifyingGap = -1.0;
                    newVehicles[i].PracticeQualifyingInterval = -1.0;
                }

                CalculateRaceClassPositions(
                    newVehicles
                );
            }

            lock (_lock)
            {
                Volatile.Write(ref _vehicles, newVehicles);

                _isConnected = true;

                _lastError = "";
            }
        }

        private static bool AreSameVehicle(LMUApiVehicle left, LMUApiVehicle right)
        {
            if (left == null || right == null ||
                string.IsNullOrWhiteSpace(left.CarNumber) ||
                string.IsNullOrWhiteSpace(right.CarNumber) ||
                !string.Equals(left.CarNumber.Trim(), right.CarNumber.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                return false;

            return string.IsNullOrWhiteSpace(left.CarClass) ||
                string.IsNullOrWhiteSpace(right.CarClass) ||
                string.Equals(left.CarClass.Trim(), right.CarClass.Trim(),
                    StringComparison.OrdinalIgnoreCase);
        }

        private static void PreserveMissingVehicleFields(
            LMUApiVehicle current,
            LMUApiVehicle previous)
        {
            if (string.IsNullOrWhiteSpace(current.DriverName))
                current.DriverName = previous.DriverName;
            if (string.IsNullOrWhiteSpace(current.TeamName))
                current.TeamName = previous.TeamName;
            if (string.IsNullOrWhiteSpace(current.CarNumber))
                current.CarNumber = previous.CarNumber;
            if (string.IsNullOrWhiteSpace(current.CarClass))
                current.CarClass = previous.CarClass;
            if (!current.HasVeFraction && previous.HasVeFraction)
                current.VeFraction = previous.VeFraction;
        }

        private static void CalculatePracticeQualifyingData(
            LMUApiVehicle[] vehicles)
        {
            Dictionary<string, List<LMUApiVehicle>>
                vehiclesByClass =
                    new Dictionary<string, List<LMUApiVehicle>>(
                        StringComparer.OrdinalIgnoreCase
                    );

            for (int i = 0; i < vehicles.Length; i++)
            {
                LMUApiVehicle vehicle =
                    vehicles[i];

                if (vehicle == null)
                    continue;

                if (!IsValidLapTime(
                        vehicle.BestLapTime))
                {
                    vehicle.PositionClass = 0;
                    continue;
                }

                string carClass =
                    vehicle.CarClass ?? "";

                List<LMUApiVehicle> classVehicles;

                if (!vehiclesByClass.TryGetValue(
                        carClass,
                        out classVehicles))
                {
                    classVehicles =
                        new List<LMUApiVehicle>();

                    vehiclesByClass.Add(
                        carClass,
                        classVehicles
                    );
                }

                classVehicles.Add(vehicle);
            }

            foreach (
                KeyValuePair<
                    string,
                    List<LMUApiVehicle>> pair
                in vehiclesByClass)
            {
                List<LMUApiVehicle> classVehicles =
                    pair.Value;

                classVehicles.Sort(
                    CompareBestLapTime
                );

                for (int i = 0;
                     i < classVehicles.Count;
                     i++)
                {
                    LMUApiVehicle vehicle =
                        classVehicles[i];

                    vehicle.PositionClass =
                        i + 1;

                    double classLeaderBestLap =
                        classVehicles[0].BestLapTime;

                    vehicle.PracticeQualifyingGap =
                        vehicle.BestLapTime -
                        classLeaderBestLap;

                    if (i > 0)
                    {
                        double nextFasterBestLap =
                            classVehicles[i - 1].BestLapTime;

                        vehicle.PracticeQualifyingInterval =
                            vehicle.BestLapTime -
                            nextFasterBestLap;
                    }
                    else
                    {
                        vehicle.PracticeQualifyingInterval =
                            0.0;
                    }

                    if (vehicle.PracticeQualifyingGap < 0.0)
                    {
                        vehicle.PracticeQualifyingGap =
                            0.0;
                    }

                    if (vehicle.PracticeQualifyingInterval < 0.0)
                    {
                        vehicle.PracticeQualifyingInterval =
                            0.0;
                    }
                }
            }
        }

        private static void CalculateRaceClassPositions(
            LMUApiVehicle[] vehicles)
        {
            Dictionary<string, int> classPositions =
                new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase
                );

            for (int i = 0;
                 i < vehicles.Length;
                 i++)
            {
                LMUApiVehicle vehicle =
                    vehicles[i];

                if (vehicle == null)
                    continue;

                string carClass =
                    vehicle.CarClass ?? "";

                int classPosition;

                if (!classPositions.TryGetValue(
                        carClass,
                        out classPosition))
                {
                    classPosition = 0;
                }

                classPosition++;

                classPositions[carClass] =
                    classPosition;

                vehicle.PositionClass =
                    classPosition;
            }
        }

        private static int CompareBestLapTime(
            LMUApiVehicle a,
            LMUApiVehicle b)
        {
            return a.BestLapTime.CompareTo(
                b.BestLapTime
            );
        }

        private static bool IsPracticeOrQualifying(
            int session)
        {
            /*
             * LMU/rFactor 2 session values:
             *
             * 0      Test Day
             * 1-4    Practice
             * 5-8    Qualifying
             * 9      Warmup
             * 10-13  Race
             */

            return
                (session >= 1 && session <= 8);
        }

        private static bool IsValidLapTime(
            double value)
        {
            return
                !double.IsNaN(value) &&
                !double.IsInfinity(value) &&
                value > 0.0;
        }

        public void Dispose()
        {
            if (_vehicleMetadataTimer != null)
            {
                _vehicleMetadataTimer.Dispose();
                _vehicleMetadataTimer = null;
            }
            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }
        }

        private sealed class TimeoutWebClient : WebClient
        {
            private readonly int _timeoutMilliseconds;

            public TimeoutWebClient(int timeoutMilliseconds)
            {
                _timeoutMilliseconds = timeoutMilliseconds;
            }

            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest request = base.GetWebRequest(address);
                request.Timeout = _timeoutMilliseconds;
                HttpWebRequest httpRequest = request as HttpWebRequest;
                if (httpRequest != null)
                    httpRequest.ReadWriteTimeout = _timeoutMilliseconds;
                return request;
            }
        }
    }

    internal sealed class LMUVehicleModelMetadata
    {
        public string Manufacturer { get; set; }
        public string Model { get; set; }
    }

    [DataContract]
    public sealed class LMURepairAndRefuelResponse
    {
        [DataMember(Name = "fuelInfo")]
        public LMURepairFuelInfo FuelInfo { get; set; }
    }

    [DataContract]
    public sealed class LMURepairFuelInfo
    {
        [DataMember(Name = "currentFuel")]
        public double CurrentFuel { get; set; }

        [DataMember(Name = "maxFuel")]
        public double MaxFuel { get; set; }
    }

    [DataContract]
    public sealed class LMUApiVehicle
    {
        private double _veFraction;

        [DataMember(Name = "position")]
        public int Position { get; set; }

        [DataMember(Name = "carNumber")]
        public string CarNumber { get; set; }

        [DataMember(Name = "carClass")]
        public string CarClass { get; set; }

        [DataMember(Name = "driverName")]
        public string DriverName { get; set; }

        [DataMember(Name = "fullTeamName")]
        public string TeamName { get; set; }

        [DataMember(Name = "bestLapTime")]
        public double BestLapTime { get; set; }

        [DataMember(Name = "lastLapTime")]
        public double LastLapTime { get; set; }

        [DataMember(Name = "timeBehindLeader")]
        public double TimeBehindLeader { get; set; }

        [DataMember(Name = "timeBehindNext")]
        public double TimeBehindNext { get; set; }

        [DataMember(Name = "timeBehindClassLeader")]
        public double TimeBehindClassLeader { get; set; }

        [DataMember(Name = "pitState")]
        public string PitState { get; set; }

        [DataMember(Name = "pitting")]
        public bool Pitting { get; set; }

        [DataMember(Name = "inGarageStall")]
        public bool InGarageStall { get; set; }

        [DataMember(Name = "veFraction")]
        public double VeFraction
        {
            get { return _veFraction; }
            set
            {
                _veFraction = value;
                HasVeFraction = true;
            }
        }

        [IgnoreDataMember]
        public bool HasVeFraction { get; private set; }

        [DataMember(Name = "fuelFraction")]
        public double FuelFraction { get; set; }

        [DataMember(Name = "penalties")]
        public int Penalties { get; set; }

        [DataMember(Name = "finishStatus")]
        public string FinishStatus { get; set; }

        public int PositionClass { get; set; }

        public double PracticeQualifyingGap { get; set; }

        public double PracticeQualifyingInterval { get; set; }

        public double GapToLeader
        {
            get
            {
                /*
                 * Practice / Qualifying:
                 * gap to fastest car in own class.
                 *
                 * Race:
                 * gap to class leader from LMU API.
                 */
                if (PracticeQualifyingGap >= 0.0)
                    return PracticeQualifyingGap;

                return TimeBehindClassLeader;
            }
        }

        public double IntervalToNext
        {
            get
            {
                /*
                 * Practice / Qualifying:
                 * difference to next faster car
                 * in the same class.
                 *
                 * Race:
                 * LMU API timeBehindNext.
                 */
                if (PracticeQualifyingInterval >= 0.0)
                    return PracticeQualifyingInterval;

                return TimeBehindNext;
            }
        }
    }
}
