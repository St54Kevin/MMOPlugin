using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace MMOPlugin
{
    [DataContract]
    internal sealed class CarTrackStatisticsDatabase
    {
        [DataMember(Order = 1)] public int SchemaVersion { get; set; }
        [DataMember(Order = 2)] public List<CarTrackStatisticsRecord> Records { get; set; }
        [DataMember(Order = 3)] public List<RaceStartPositionSnapshot> RaceStartSnapshots { get; set; }
    }

    [DataContract]
    internal sealed class RaceStartPosition
    {
        [DataMember(Order = 1)] public int CarId { get; set; }
        [DataMember(Order = 2)] public int GridPosition { get; set; }
    }

    [DataContract]
    internal sealed class RaceStartPositionSnapshot
    {
        [DataMember(Order = 1)] public string TrackName { get; set; }
        [DataMember(Order = 2)] public int Session { get; set; }
        [DataMember(Order = 3)] public double StartET { get; set; }
        [DataMember(Order = 4)] public List<RaceStartPosition> Vehicles { get; set; }
        [DataMember(Order = 5)] public string UpdatedUtc { get; set; }
        [DataMember(Order = 6)] public bool IsFrozen { get; set; }
    }

    [DataContract]
    internal sealed class CarTrackStatisticsRecord
    {
        [DataMember(Order = 1)] public string CarKey { get; set; }
        [DataMember(Order = 2)] public string CarName { get; set; }
        [DataMember(Order = 3)] public string TrackKey { get; set; }
        [DataMember(Order = 4)] public string TrackName { get; set; }
        [DataMember(Order = 5)] public int CompletedLaps { get; set; }
        [DataMember(Order = 6)] public double BestLapSeconds { get; set; }
        [DataMember(Order = 14)] public string BestLapTimestampUtc { get; set; }
        [DataMember(Order = 7)] public double FuelTotal { get; set; }
        [DataMember(Order = 8)] public int FuelSampleCount { get; set; }
        [DataMember(Order = 9)] public double VeTotal { get; set; }
        [DataMember(Order = 10)] public int VeSampleCount { get; set; }
        [DataMember(Order = 11)] public List<double> LastFuelLaps { get; set; }
        [DataMember(Order = 12)] public List<double> LastVeLaps { get; set; }
        [DataMember(Order = 13)] public string UpdatedUtc { get; set; }
        [DataMember(Order = 15)] public double FuelTarget { get; set; }
        [DataMember(Order = 16)] public bool HasFuelTarget { get; set; }
        [DataMember(Order = 17)] public bool HasVirtualEnergy { get; set; }
        [DataMember(Order = 18)] public bool FuelTargetUsesVirtualEnergy { get; set; }
        [DataMember(Order = 19)] public string Manufacturer { get; set; }
        [DataMember(Order = 20)] public string Model { get; set; }
        [DataMember(Order = 21)] public string VehicleModel { get; set; }

        public double AverageFuel
        {
            get { return FuelSampleCount > 0 ? FuelTotal / FuelSampleCount : 0.0; }
        }

        public double AverageVe
        {
            get { return VeSampleCount > 0 ? VeTotal / VeSampleCount : 0.0; }
        }

        public double FuelFallbackAverage
        {
            get
            {
                if (LastFuelLaps == null || LastFuelLaps.Count < 5) return 0.0;
                if (LastFuelLaps.Count < 10)
                    return LastFuelLaps.Take(5).Average();
                return LastFuelLaps.Skip(LastFuelLaps.Count - 10).Average();
            }
        }

        public double LastFiveVeAverage
        {
            get { return Average(LastVeLaps); }
        }

        private static double Average(List<double> values)
        {
            if (values == null || values.Count == 0) return 0.0;
            return values.Average();
        }
    }

    internal sealed class CarTrackStatisticsStore
    {
        private const int CurrentSchemaVersion = 1;
        private readonly object _gate = new object();
        private readonly string _filePath;
        private CarTrackStatisticsDatabase _database;

        public CarTrackStatisticsStore(string filePath)
        {
            _filePath = filePath;
        }

        public string FilePath { get { return _filePath; } }

        public void LoadOrCreate()
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath));
                if (File.Exists(_filePath))
                {
                    try
                    {
                        _database = ReadDatabase(_filePath);
                        int recordCountBeforeCleanup = _database == null || _database.Records == null
                            ? 0
                            : _database.Records.Count;
                        Validate(_database);
                        if (_database.Records.Count != recordCountBeforeCleanup)
                        {
                            // Preserve the original file before removing stale
                            // zero-lap entries from the statistics database.
                            CreateAutomaticBackup();
                            WriteDatabaseAtomically(_database, _filePath);
                        }
                        return;
                    }
                    catch
                    {
                        string corruptPath = _filePath + ".corrupt-" +
                            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                        try { File.Copy(_filePath, corruptPath, false); } catch { }
                    }
                }

                _database = NewDatabase();
                WriteDatabaseAtomically(_database, _filePath);
            }
        }

        public CarTrackStatisticsRecord RecordCompletedLap(
            string carKey,
            string carName,
            string trackName,
            double lapTimeSeconds,
            bool hasFuelSample,
            double fuelUsed,
            bool hasVeSample,
            double veUsed,
            string manufacturer,
            string model,
            string vehicleModel)
        {
            if (string.IsNullOrWhiteSpace(carKey) || string.IsNullOrWhiteSpace(trackName))
                return null;

            lock (_gate)
            {
                EnsureLoaded();
                CarTrackStatisticsRecord record = FindOrCreate(carKey, carName, trackName);
                if (!string.IsNullOrWhiteSpace(manufacturer)) record.Manufacturer = manufacturer.Trim();
                if (!string.IsNullOrWhiteSpace(model)) record.Model = model.Trim();
                if (!string.IsNullOrWhiteSpace(vehicleModel))
                {
                    record.VehicleModel = vehicleModel.Trim();
                    record.CarName = vehicleModel.Trim();
                }
                record.CompletedLaps++;
                if (IsFinitePositive(lapTimeSeconds) &&
                    (record.BestLapSeconds <= 0.0 || lapTimeSeconds < record.BestLapSeconds))
                {
                    record.BestLapSeconds = lapTimeSeconds;
                    record.BestLapTimestampUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                }

                if (hasFuelSample && IsFinitePositive(fuelUsed))
                {
                    record.FuelTotal += fuelUsed;
                    record.FuelSampleCount++;
                    AddLastTen(record.LastFuelLaps, fuelUsed);
                }
                if (hasVeSample && IsFinitePositive(veUsed))
                {
                    record.VeTotal += veUsed;
                    record.VeSampleCount++;
                    AddLastFive(record.LastVeLaps, veUsed);
                    record.HasVirtualEnergy = true;
                }

                record.UpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                WriteDatabaseAtomically(_database, _filePath);
                return Clone(record);
            }
        }

        public CarTrackStatisticsRecord GetRecord(string carKey, string trackName)
        {
            return GetRecord(carKey, trackName, null);
        }

        public CarTrackStatisticsRecord GetRecord(
            string carKey,
            string trackName,
            string legacyVehicleFile)
        {
            if (string.IsNullOrWhiteSpace(carKey) || string.IsNullOrWhiteSpace(trackName)) return null;
            lock (_gate)
            {
                EnsureLoaded();
                CarTrackStatisticsRecord record = _database.Records.FirstOrDefault(item =>
                    string.Equals(item.CarKey, carKey, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.TrackKey, trackName, StringComparison.OrdinalIgnoreCase));
                CarTrackStatisticsRecord legacyRecord = null;
                if (!string.IsNullOrWhiteSpace(legacyVehicleFile))
                {
                    string legacyPrefix = legacyVehicleFile.Trim() + "|";
                    legacyRecord = _database.Records
                        .Where(item => item != null &&
                            item.CarKey != null &&
                            item.CarKey.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(item.TrackKey, trackName, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(item => item.FuelFallbackAverage > 0.0 || item.LastFiveVeAverage > 0.0)
                        .ThenByDescending(item => item.CompletedLaps)
                        .FirstOrDefault();
                }

                // Prefer a record with saved consumption history. This keeps
                // earlier data available while a new canonical model key is
                // still accumulating its first complete laps.
                if (record != null &&
                    (record.FuelFallbackAverage > 0.0 || record.LastFiveVeAverage > 0.0))
                    return Clone(record);
                return legacyRecord != null ? Clone(legacyRecord) : record == null ? null : Clone(record);
            }
        }

        public void MarkVehicleHasVirtualEnergy(string carKey, string carName, string trackName)
        {
            if (string.IsNullOrWhiteSpace(carKey) || string.IsNullOrWhiteSpace(trackName))
                return;

            lock (_gate)
            {
                EnsureLoaded();
                CarTrackStatisticsRecord record = FindOrCreate(carKey, carName, trackName);
                if (record.HasVirtualEnergy) return;
                record.HasVirtualEnergy = true;
                record.UpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                WriteDatabaseAtomically(_database, _filePath);
            }
        }

        public void UpdateVehicleMetadata(
            string carKey,
            string legacyVehicleFile,
            string trackName,
            string manufacturer,
            string model,
            string displayName)
        {
            if (string.IsNullOrWhiteSpace(trackName) ||
                string.IsNullOrWhiteSpace(manufacturer) ||
                string.IsNullOrWhiteSpace(model)) return;
            lock (_gate)
            {
                EnsureLoaded();
                CarTrackStatisticsRecord record = _database.Records.FirstOrDefault(item =>
                    string.Equals(item.CarKey, carKey, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.TrackKey, trackName, StringComparison.OrdinalIgnoreCase));
                if (record == null && !string.IsNullOrWhiteSpace(legacyVehicleFile))
                {
                    string prefix = legacyVehicleFile.Trim() + "|";
                    record = _database.Records
                        .Where(item => item.CarKey != null &&
                            item.CarKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(item.TrackKey, trackName, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(item => item.CompletedLaps)
                        .FirstOrDefault();
                }
                if (record == null) return;

                string normalizedDisplayName = string.IsNullOrWhiteSpace(displayName)
                    ? manufacturer.Trim() + " " + model.Trim()
                    : displayName.Trim();
                if (string.Equals(record.Manufacturer, manufacturer.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(record.Model, model.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(record.CarName, normalizedDisplayName, StringComparison.OrdinalIgnoreCase))
                    return;

                record.Manufacturer = manufacturer.Trim();
                record.Model = model.Trim();
                record.CarName = normalizedDisplayName;
                record.UpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                WriteDatabaseAtomically(_database, _filePath);
            }
        }

        public void UpdateVehicleModelName(
            string vehicleFile,
            string vehicleName,
            string statisticsCarKey,
            string vehicleModel)
        {
            if (string.IsNullOrWhiteSpace(vehicleModel)) return;
            string fileIdentity = NormalizeVehicleIdentity(vehicleFile);
            string nameIdentity = NormalizeVehicleIdentity(vehicleName);
            string keyIdentity = NormalizeVehicleIdentity(statisticsCarKey);
            if (string.IsNullOrEmpty(fileIdentity) && string.IsNullOrEmpty(nameIdentity) &&
                string.IsNullOrEmpty(keyIdentity)) return;

            lock (_gate)
            {
                EnsureLoaded();
                bool changed = false;
                foreach (CarTrackStatisticsRecord record in _database.Records)
                {
                    if (record == null) continue;
                    string storedKey = NormalizeVehicleIdentity((record.CarKey ?? "").Split('|')[0]);
                    string storedName = NormalizeVehicleIdentity(record.CarName);
                    bool matches =
                        (!string.IsNullOrEmpty(fileIdentity) && storedKey == fileIdentity) ||
                        (!string.IsNullOrEmpty(keyIdentity) && storedKey == keyIdentity) ||
                        (!string.IsNullOrEmpty(nameIdentity) &&
                            (storedKey == nameIdentity || storedName == nameIdentity));
                    if (!matches || string.Equals(record.VehicleModel, vehicleModel.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;

                    record.VehicleModel = vehicleModel.Trim();
                    record.CarName = vehicleModel.Trim();
                    record.UpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    changed = true;
                }

                if (changed) WriteDatabaseAtomically(_database, _filePath);
            }
        }

        public void SetFuelTarget(
            string carKey,
            string carName,
            string trackName,
            double fuelTarget,
            bool usesVirtualEnergy)
        {
            if (string.IsNullOrWhiteSpace(carKey) || string.IsNullOrWhiteSpace(trackName) ||
                double.IsNaN(fuelTarget) || double.IsInfinity(fuelTarget))
                return;

            lock (_gate)
            {
                EnsureLoaded();
                CarTrackStatisticsRecord record = FindOrCreate(carKey, carName, trackName);
                record.FuelTarget = fuelTarget;
                record.HasFuelTarget = true;
                record.FuelTargetUsesVirtualEnergy = usesVirtualEnergy;
                if (usesVirtualEnergy) record.HasVirtualEnergy = true;
                record.UpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                WriteDatabaseAtomically(_database, _filePath);
            }
        }

        public RaceStartPositionSnapshot GetRaceStartSnapshot(
            string trackName,
            int session,
            double startET)
        {
            if (string.IsNullOrWhiteSpace(trackName)) return null;
            lock (_gate)
            {
                EnsureLoaded();
                RaceStartPositionSnapshot snapshot = _database.RaceStartSnapshots.FirstOrDefault(item =>
                    item.Session == session &&
                    string.Equals(item.TrackName, trackName, StringComparison.OrdinalIgnoreCase));
                if (snapshot == null) return null;
                if (IsFinite(startET) && IsFinite(snapshot.StartET) &&
                    Math.Abs(snapshot.StartET - startET) > 1.0)
                    return null;
                return Clone(snapshot);
            }
        }

        public void SaveRaceStartSnapshot(
            string trackName,
            int session,
            double startET,
            List<RaceStartPosition> vehicles,
            bool freeze)
        {
            if (string.IsNullOrWhiteSpace(trackName) || vehicles == null || vehicles.Count == 0)
                return;
            lock (_gate)
            {
                EnsureLoaded();
                RaceStartPositionSnapshot snapshot = _database.RaceStartSnapshots.FirstOrDefault(item =>
                    item.Session == session &&
                    string.Equals(item.TrackName, trackName, StringComparison.OrdinalIgnoreCase));
                double normalizedStartET = IsFinite(startET) ? startET : 0.0;
                if (snapshot != null &&
                    Math.Abs(snapshot.StartET - normalizedStartET) > 1.0)
                {
                    _database.RaceStartSnapshots.Remove(snapshot);
                    snapshot = null;
                }
                if (snapshot == null)
                {
                    snapshot = new RaceStartPositionSnapshot
                    {
                        TrackName = trackName.Trim(),
                        Session = session,
                        Vehicles = new List<RaceStartPosition>()
                    };
                    _database.RaceStartSnapshots.Add(snapshot);
                }
                else if (snapshot.IsFrozen)
                {
                    return;
                }

                bool changed = !SamePositions(snapshot.Vehicles, vehicles) ||
                    Math.Abs(snapshot.StartET - normalizedStartET) > 1.0 ||
                    (freeze && !snapshot.IsFrozen);
                if (!changed) return;

                snapshot.StartET = normalizedStartET;
                snapshot.Vehicles = vehicles
                    .Where(item => item != null && item.CarId >= 0 && item.GridPosition > 0)
                    .GroupBy(item => item.CarId)
                    .Select(group => new RaceStartPosition
                    {
                        CarId = group.Key,
                        GridPosition = group.First().GridPosition
                    })
                    .ToList();
                snapshot.UpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                snapshot.IsFrozen = freeze;
                WriteDatabaseAtomically(_database, _filePath);
            }
        }

        public List<CarTrackStatisticsRecord> GetRecords()
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _database.Records.Select(Clone)
                    .OrderBy(item => item.CarName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.TrackName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        public void DeleteRecord(string carKey, string trackKey)
        {
            if (string.IsNullOrWhiteSpace(carKey) || string.IsNullOrWhiteSpace(trackKey))
                return;
            lock (_gate)
            {
                EnsureLoaded();
                int removed = _database.Records.RemoveAll(item =>
                    string.Equals(item.CarKey, carKey, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.TrackKey, trackKey, StringComparison.OrdinalIgnoreCase));
                if (removed > 0) WriteDatabaseAtomically(_database, _filePath);
            }
        }

        public void DeleteAllRecords()
        {
            lock (_gate)
            {
                EnsureLoaded();
                if (_database.Records.Count == 0) return;
                _database.Records.Clear();
                WriteDatabaseAtomically(_database, _filePath);
            }
        }

        public void ExportTo(string destination)
        {
            lock (_gate)
            {
                EnsureLoaded();
                WriteDatabaseAtomically(_database, destination);
            }
        }

        public void ImportFrom(string source)
        {
            CarTrackStatisticsDatabase imported = ReadDatabase(source);
            Validate(imported);
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath));
                _database = imported;
                WriteDatabaseAtomically(_database, _filePath);
            }
        }

        public void CreateAutomaticBackup()
        {
            lock (_gate)
            {
                EnsureLoaded();
                if (!File.Exists(_filePath))
                    WriteDatabaseAtomically(_database, _filePath);
                string backupPath = _filePath + ".auto-backup.json";
                string tempPath = backupPath + ".tmp";
                File.Copy(_filePath, tempPath, true);
                ReplaceFile(tempPath, backupPath);
            }
        }

        private void EnsureLoaded()
        {
            if (_database == null) LoadOrCreate();
        }

        private CarTrackStatisticsRecord FindOrCreate(string carKey, string carName, string trackName)
        {
            string trackKey = trackName.Trim();
            CarTrackStatisticsRecord record = _database.Records.FirstOrDefault(item =>
                string.Equals(item.CarKey, carKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.TrackKey, trackKey, StringComparison.OrdinalIgnoreCase));
            if (record != null)
            {
                if (!string.IsNullOrWhiteSpace(carName)) record.CarName = carName.Trim();
                record.TrackName = trackKey;
                return record;
            }

            record = new CarTrackStatisticsRecord
            {
                CarKey = carKey.Trim(),
                CarName = string.IsNullOrWhiteSpace(carName) ? carKey.Trim() : carName.Trim(),
                TrackKey = trackKey,
                TrackName = trackKey,
                LastFuelLaps = new List<double>(),
                LastVeLaps = new List<double>()
            };
            _database.Records.Add(record);
            return record;
        }

        private static void AddLastTen(List<double> values, double value)
        {
            if (values == null) return;
            values.Add(value);
            while (values.Count > 10) values.RemoveAt(0);
        }

        private static void AddLastFive(List<double> values, double value)
        {
            if (values == null) return;
            values.Add(value);
            while (values.Count > 5) values.RemoveAt(0);
        }

        private static bool SamePositions(
            List<RaceStartPosition> current,
            List<RaceStartPosition> incoming)
        {
            if (current == null || incoming == null || current.Count != incoming.Count)
                return false;
            Dictionary<int, int> byId = current.ToDictionary(item => item.CarId, item => item.GridPosition);
            foreach (RaceStartPosition item in incoming)
            {
                int position;
                if (item == null || !byId.TryGetValue(item.CarId, out position) ||
                    position != item.GridPosition)
                    return false;
            }
            return true;
        }

        private static CarTrackStatisticsDatabase NewDatabase()
        {
            return new CarTrackStatisticsDatabase
            {
                SchemaVersion = CurrentSchemaVersion,
                Records = new List<CarTrackStatisticsRecord>(),
                RaceStartSnapshots = new List<RaceStartPositionSnapshot>()
            };
        }

        private static RaceStartPositionSnapshot Clone(RaceStartPositionSnapshot value)
        {
            return new RaceStartPositionSnapshot
            {
                TrackName = value.TrackName,
                Session = value.Session,
                StartET = value.StartET,
                UpdatedUtc = value.UpdatedUtc,
                IsFrozen = value.IsFrozen,
                Vehicles = value.Vehicles == null
                    ? new List<RaceStartPosition>()
                    : value.Vehicles.Select(item => new RaceStartPosition
                    {
                        CarId = item.CarId,
                        GridPosition = item.GridPosition
                    }).ToList()
            };
        }

        private static CarTrackStatisticsRecord Clone(CarTrackStatisticsRecord value)
        {
            return new CarTrackStatisticsRecord
            {
                CarKey = value.CarKey,
                CarName = value.CarName,
                TrackKey = value.TrackKey,
                TrackName = value.TrackName,
                CompletedLaps = value.CompletedLaps,
                BestLapSeconds = value.BestLapSeconds,
                BestLapTimestampUtc = value.BestLapTimestampUtc,
                FuelTotal = value.FuelTotal,
                FuelSampleCount = value.FuelSampleCount,
                VeTotal = value.VeTotal,
                VeSampleCount = value.VeSampleCount,
                LastFuelLaps = value.LastFuelLaps == null ? new List<double>() : new List<double>(value.LastFuelLaps),
                LastVeLaps = value.LastVeLaps == null ? new List<double>() : new List<double>(value.LastVeLaps),
                UpdatedUtc = value.UpdatedUtc,
                FuelTarget = value.FuelTarget,
                HasFuelTarget = value.HasFuelTarget,
                HasVirtualEnergy = value.HasVirtualEnergy,
                FuelTargetUsesVirtualEnergy = value.FuelTargetUsesVirtualEnergy,
                Manufacturer = value.Manufacturer,
                Model = value.Model,
                VehicleModel = value.VehicleModel
            };
        }

        private static string NormalizeVehicleIdentity(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity)) return "";
            StringBuilder normalized = new StringBuilder(identity.Length);
            foreach (char character in identity)
                if (char.IsLetterOrDigit(character))
                    normalized.Append(char.ToLowerInvariant(character));
            return normalized.ToString();
        }

        private static CarTrackStatisticsDatabase ReadDatabase(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                DataContractJsonSerializer serializer = CreateSerializer();
                return (CarTrackStatisticsDatabase)serializer.ReadObject(stream);
            }
        }

        private static void Validate(CarTrackStatisticsDatabase database)
        {
            if (database == null || database.SchemaVersion != CurrentSchemaVersion || database.Records == null)
                throw new InvalidDataException("The selected file is not a supported MMO Plugin data backup.");
            if (database.Records.Count > 10000)
                throw new InvalidDataException("The data file contains too many car/track combinations.");
            if (database.RaceStartSnapshots == null)
                database.RaceStartSnapshots = new List<RaceStartPositionSnapshot>();
            if (database.RaceStartSnapshots.Count > 10000)
                throw new InvalidDataException("The data file contains too many saved race grids.");

            foreach (CarTrackStatisticsRecord record in database.Records)
            {
                if (record == null || string.IsNullOrWhiteSpace(record.CarKey) ||
                    string.IsNullOrWhiteSpace(record.TrackKey) || record.CompletedLaps < 0 ||
                    record.FuelSampleCount < 0 || record.VeSampleCount < 0 ||
                    !IsFiniteNonNegative(record.BestLapSeconds) ||
                    !IsFiniteNonNegative(record.FuelTotal) || !IsFiniteNonNegative(record.VeTotal))
                    throw new InvalidDataException("The data file contains an invalid record.");
                if (string.IsNullOrWhiteSpace(record.CarName)) record.CarName = record.CarKey;
                if (string.IsNullOrWhiteSpace(record.TrackName)) record.TrackName = record.TrackKey;
                if (record.LastFuelLaps == null) record.LastFuelLaps = new List<double>();
                if (record.LastVeLaps == null) record.LastVeLaps = new List<double>();
                if (double.IsNaN(record.FuelTarget) || double.IsInfinity(record.FuelTarget))
                    record.FuelTarget = 0.0;
                NormalizeSamples(record.LastFuelLaps, 10);
                NormalizeSamples(record.LastVeLaps, 5);
            }

            database.Records.RemoveAll(record => record.CompletedLaps == 0);

            foreach (RaceStartPositionSnapshot snapshot in database.RaceStartSnapshots)
            {
                if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.TrackName) ||
                    snapshot.Session < 0 || snapshot.Vehicles == null ||
                    !IsFiniteNonNegative(Math.Abs(snapshot.StartET)))
                    throw new InvalidDataException("The data file contains an invalid race grid.");
                foreach (RaceStartPosition vehicle in snapshot.Vehicles)
                    if (vehicle == null || vehicle.CarId < 0 || vehicle.GridPosition < 1)
                        throw new InvalidDataException("The data file contains an invalid race-grid vehicle.");
            }
        }

        private static void NormalizeSamples(List<double> values, int maxCount)
        {
            if (values == null) return;
            if (values.Count > maxCount) throw new InvalidDataException("The data file contains an invalid consumption history.");
            if (values.Any(value => !IsFinitePositive(value)))
                throw new InvalidDataException("The data file contains an invalid consumption value.");
        }

        private static bool IsFinitePositive(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0.0;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool IsFiniteNonNegative(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0.0;
        }

        private static void WriteDatabaseAtomically(CarTrackStatisticsDatabase database, string path)
        {
            bool measurePerformance = PluginPerformanceDiagnostics.Enabled;
            long performanceStartedAt = measurePerformance
                ? System.Diagnostics.Stopwatch.GetTimestamp()
                : 0L;
            long allocatedBytesAtStart = measurePerformance
                ? GC.GetAllocatedBytesForCurrentThread()
                : 0L;
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                string tempPath = path + ".tmp";
                using (FileStream stream = File.Create(tempPath))
                {
                    DataContractJsonSerializer serializer = CreateSerializer();
                    serializer.WriteObject(stream, database);
                    stream.Flush(true);
                }
                ReplaceFile(tempPath, path);
            }
            finally
            {
                if (measurePerformance)
                    PluginPerformanceDiagnostics.StatisticsFileWrite.Add(performanceStartedAt, allocatedBytesAtStart);
            }
        }

        private static DataContractJsonSerializer CreateSerializer()
        {
            return new DataContractJsonSerializer(
                typeof(CarTrackStatisticsDatabase),
                new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 500000 }
            );
        }

        private static void ReplaceFile(string source, string destination)
        {
            if (File.Exists(destination))
            {
                try
                {
                    File.Replace(source, destination, null);
                    return;
                }
                catch { }
            }
            File.Copy(source, destination, true);
            File.Delete(source);
        }
    }
}
