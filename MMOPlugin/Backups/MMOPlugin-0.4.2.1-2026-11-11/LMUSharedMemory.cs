using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace MMOPlugin
{
    public sealed class LMUSharedMemory : IDisposable
    {
        private const string SharedMemoryName = "LMU_Data";

        private const long ScoringOffset = 1632;

        private const int ScoringStreamSize = 12;

        private const int MaxVehicles = 62;

        private const int MaxMappedVehicles = 104;

        private const int ResultsStreamSize = 65536;

        private const int TelemetryHeaderSize = 4;

        private const int TelemetryHeaderPlayerIndexOffset = 1;

        private const int TelemetryHeaderPlayerHasVehicleOffset = 2;

        private const int ExpectedScoringInfoSize = 548;

        private const int ExpectedVehicleScoringSize = 584;

        private const int VehicleScoringIdOffset = 0;

        private const int VehicleScoringDriverNameOffset = 4;

        private const int VehicleScoringVehicleNameOffset = 36;

        private const int VehicleScoringTotalLapsOffset = 100;

        private const int VehicleScoringSectorOffset = 102;

        private const int VehicleScoringFinishStatusOffset = 103;

        private const int VehicleScoringLapDistOffset = 104;

        private const int VehicleScoringPathLateralOffset = 112;

        private const int VehicleScoringTrackEdgeOffset = 120;

        private const int VehicleScoringBestSector1Offset = 128;

        private const int VehicleScoringBestSector2Offset = 136;

        private const int VehicleScoringBestLapTimeOffset = 144;

        private const int VehicleScoringLastSector1Offset = 152;

        private const int VehicleScoringLastSector2Offset = 160;

        private const int VehicleScoringLastLapTimeOffset = 168;

        private const int VehicleScoringCurSector1Offset = 176;

        private const int VehicleScoringCurSector2Offset = 184;

        private const int VehicleScoringNumPitstopsOffset = 192;

        private const int VehicleScoringNumPenaltiesOffset = 194;

        private const int VehicleScoringIsPlayerOffset = 196;

        private const int VehicleScoringControlOffset = 197;

        private const int VehicleScoringInPitsOffset = 198;

        private const int VehicleScoringPlaceOffset = 199;

        private const int VehicleScoringVehicleClassOffset = 200;

        private const int VehicleScoringTimeBehindNextOffset = 232;

        private const int VehicleScoringLapsBehindNextOffset = 240;

        private const int VehicleScoringTimeBehindLeaderOffset = 244;

        private const int VehicleScoringLapsBehindLeaderOffset = 252;

        private const int VehicleScoringLapStartETOffset = 256;

        private const int VehicleScoringPositionOffset = 264;

        private const int VehicleScoringLocalVelocityOffset = 288;

        private const int VehicleScoringLocalAccelerationOffset = 312;

        private const int VehicleScoringOrientationOffset = 336;

        private const int VehicleScoringLocalRotationOffset = 408;

        private const int VehicleScoringLocalRotationAccelerationOffset = 432;

        private const int VehicleScoringHeadlightsOffset = 456;

        private const int VehicleScoringPitStateOffset = 457;

        private const int VehicleScoringServerScoredOffset = 458;

        private const int VehicleScoringIndividualPhaseOffset = 459;

        private const int VehicleScoringQualificationOffset = 460;

        private const int VehicleScoringTimeIntoLapOffset = 464;

        private const int VehicleScoringEstimatedLapTimeOffset = 472;

        private const int VehicleScoringPitGroupOffset = 480;

        private const int VehicleScoringFlagOffset = 504;

        private const int VehicleScoringUnderYellowOffset = 505;

        private const int VehicleScoringCountLapFlagOffset = 506;

        private const int VehicleScoringInGarageStallOffset = 507;

        private const int VehicleScoringUpgradePackOffset = 508;

        private const int VehicleScoringPitLapDistOffset = 524;

        private const int VehicleScoringBestLapSector1Offset = 528;

        private const int VehicleScoringBestLapSector2Offset = 532;

        private const int VehicleScoringSteamIdOffset = 536;

        private const int VehicleScoringVehicleFileOffset = 544;

        private const int VehicleScoringAttackModeOffset = 576;

        private const int VehicleScoringFuelFractionOffset = 578;

        private const int VehicleScoringDrsStateOffset = 579;

        private const int VehicleScoringExpansionOffset = 580;

        private const int ExpectedVehicleTelemetryRelativeSize = 1888;

        private const int ExpectedTelemetryVehicleIdOffset = 0;

        private const int ExpectedTelemetryLapNumberOffset = 20;

        private const int ExpectedTelemetryFuelOffset = 524;

        private const int ExpectedTelemetryLapStartETOffset = 24;

        private const int ExpectedTrackLimitsStepsOffset = 767;

        private const int ExpectedTimeGapCarAheadOffset = 780;

        private const int ExpectedTimeGapCarBehindOffset = 784;

        private const int ExpectedTimeGapPlaceAheadOffset = 788;

        private const int ExpectedTimeGapPlaceBehindOffset = 792;

        private const int ExpectedVehicleModelOffset = 796;

        private const int ExpectedVehicleModelSize = 30;

        private const int ExpectedLapStartETOffset = 24;

        private const int ExpectedTyreCompFLOffset = 1089;

        private const int ExpectedTyreCompFROffset = 1349;

        private const int ExpectedTyreCompRLOffset = 1609;

        private const int ExpectedTyreCompRROffset = 1869;

        private const int ExpectedTyreWearFLOffset = 1000;

        private const int ExpectedTyreWearFROffset = 1260;

        private const int ExpectedTyreWearRLOffset = 1520;

        private const int ExpectedTyreWearRROffset = 1780;

        private MemoryMappedFile _memoryMappedFile;

        private MemoryMappedViewAccessor _viewAccessor;

        // Shared-memory reads are serialized by the plugin's data-update lock.
        // Reuse their managed and native buffers instead of allocating both for
        // every vehicle on every SimHub update.
        private byte[] _structReadBuffer;

        private IntPtr _structReadPointer = IntPtr.Zero;

        private readonly LMUVehicleScoring[] _vehicleScoringArrayBuffers =
            new LMUVehicleScoring[MaxVehicles];

        // PtrToStructure allocates a managed representation of the full 1,888-byte
        // telemetry record on every call. Only a small set of fields is consumed,
        // so read those fields directly and reuse the model-name buffers by slot.
        private readonly byte[][] _telemetryModelBuffers =
            new byte[MaxMappedVehicles][];

        private DateTime _nextConnectionAttempt =
            DateTime.MinValue;

        private string _lastError = "";

        public bool IsConnected
        {
            get
            {
                return _memoryMappedFile != null &&
                       _viewAccessor != null;
            }
        }

        public string LastError
        {
            get
            {
                return _lastError;
            }
        }

        public void UpdateConnection()
        {
            if (IsConnected)
                return;

            if (DateTime.UtcNow < _nextConnectionAttempt)
                return;

            _nextConnectionAttempt =
                DateTime.UtcNow.AddSeconds(1);

            try
            {
                _memoryMappedFile =
                    MemoryMappedFile.OpenExisting(
                        SharedMemoryName,
                        MemoryMappedFileRights.Read
                    );

                _viewAccessor =
                    _memoryMappedFile.CreateViewAccessor(
                        0,
                        0,
                        MemoryMappedFileAccess.Read
                    );

                int scoringInfoSize =
                    Marshal.SizeOf(typeof(LMUScoringInfo));

                int vehicleScoringSize =
                    Marshal.SizeOf(typeof(LMUVehicleScoring));

                int telemetrySize =
                    Marshal.SizeOf(typeof(LMUVehicleTelemetryRelative));

                ValidateVehicleScoringOffsets(vehicleScoringSize);

                int telemetryIdOffset = OffsetOf<LMUVehicleTelemetryRelative>("mID");
                int telemetryLapStartOffset = OffsetOf<LMUVehicleTelemetryRelative>("mLapStartET");

                int trackLimitsStepsOffset =
                    Marshal.OffsetOf(
                        typeof(LMUVehicleTelemetryRelative),
                        "mTrackLimitsSteps"
                    ).ToInt32();

                int timeGapAheadOffset =
                    Marshal.OffsetOf(
                        typeof(LMUVehicleTelemetryRelative),
                        "mTimeGapCarAhead"
                    ).ToInt32();

                int timeGapBehindOffset =
                    Marshal.OffsetOf(
                        typeof(LMUVehicleTelemetryRelative),
                        "mTimeGapCarBehind"
                    ).ToInt32();

                int timeGapPlaceAheadOffset =
                    Marshal.OffsetOf(
                        typeof(LMUVehicleTelemetryRelative),
                        "mTimeGapPlaceAhead"
                    ).ToInt32();

                int timeGapPlaceBehindOffset =
                    Marshal.OffsetOf(
                        typeof(LMUVehicleTelemetryRelative),
                        "mTimeGapPlaceBehind"
                    ).ToInt32();

                int vehicleModelOffset =
                    Marshal.OffsetOf(
                        typeof(LMUVehicleTelemetryRelative),
                        "mVehicleModel"
                    ).ToInt32();

                int lapStartETOffset =
                    Marshal.OffsetOf(
                        typeof(LMUVehicleTelemetryRelative),
                        "mLapStartET"
                    ).ToInt32();

                int tyreCompFLOffset =
                    Marshal.OffsetOf(
                        typeof(LMUVehicleTelemetryRelative),
                        "mTyreCompFL"
                    ).ToInt32();

                int tyreCompFROffset =
                    Marshal.OffsetOf(
                        typeof(LMUVehicleTelemetryRelative),
                        "mTyreCompFR"
                    ).ToInt32();

                int tyreCompRLOffset =
                    Marshal.OffsetOf(
                        typeof(LMUVehicleTelemetryRelative),
                        "mTyreCompRL"
                    ).ToInt32();

                int tyreCompRROffset =
                    Marshal.OffsetOf(
                        typeof(LMUVehicleTelemetryRelative),
                        "mTyreCompRR"
                    ).ToInt32();

                int telemetryLapNumberOffset = Marshal.OffsetOf(
                    typeof(LMUVehicleTelemetryRelative), "mLapNumber").ToInt32();
                int telemetryFuelOffset = Marshal.OffsetOf(
                    typeof(LMUVehicleTelemetryRelative), "mFuel").ToInt32();
                int tyreWearFLOffset = Marshal.OffsetOf(
                    typeof(LMUVehicleTelemetryRelative), "mTyreWearFL").ToInt32();
                int tyreWearFROffset = Marshal.OffsetOf(
                    typeof(LMUVehicleTelemetryRelative), "mTyreWearFR").ToInt32();
                int tyreWearRLOffset = Marshal.OffsetOf(
                    typeof(LMUVehicleTelemetryRelative), "mTyreWearRL").ToInt32();
                int tyreWearRROffset = Marshal.OffsetOf(
                    typeof(LMUVehicleTelemetryRelative), "mTyreWearRR").ToInt32();

                if (scoringInfoSize != ExpectedScoringInfoSize ||
                    vehicleScoringSize != ExpectedVehicleScoringSize ||
                    telemetrySize != ExpectedVehicleTelemetryRelativeSize ||
                    trackLimitsStepsOffset != ExpectedTrackLimitsStepsOffset ||
                    telemetryIdOffset != ExpectedTelemetryVehicleIdOffset ||
                    telemetryLapStartOffset != ExpectedTelemetryLapStartETOffset ||
                    timeGapAheadOffset != ExpectedTimeGapCarAheadOffset ||
                    timeGapBehindOffset != ExpectedTimeGapCarBehindOffset ||
                    timeGapPlaceAheadOffset != ExpectedTimeGapPlaceAheadOffset ||
                    timeGapPlaceBehindOffset != ExpectedTimeGapPlaceBehindOffset ||
                    vehicleModelOffset != ExpectedVehicleModelOffset ||
                    lapStartETOffset != ExpectedLapStartETOffset ||
                    tyreCompFLOffset != ExpectedTyreCompFLOffset ||
                    tyreCompFROffset != ExpectedTyreCompFROffset ||
                    tyreCompRLOffset != ExpectedTyreCompRLOffset ||
                    tyreCompRROffset != ExpectedTyreCompRROffset ||
                    telemetryLapNumberOffset != ExpectedTelemetryLapNumberOffset ||
                    telemetryFuelOffset != ExpectedTelemetryFuelOffset ||
                    tyreWearFLOffset != ExpectedTyreWearFLOffset ||
                    tyreWearFROffset != ExpectedTyreWearFROffset ||
                    tyreWearRLOffset != ExpectedTyreWearRLOffset ||
                    tyreWearRROffset != ExpectedTyreWearRROffset)
                {
                    throw new InvalidOperationException(
                        "LMU structure size/offset mismatch. " +
                        "ScoringInfo=" + scoringInfoSize +
                        ", VehicleScoring=" + vehicleScoringSize +
                        ", Telemetry=" + telemetrySize +
                        ", TrackLimitsStepsOffset=" + trackLimitsStepsOffset +
                        ", TelemetryIdOffset=" + telemetryIdOffset +
                        ", TelemetryLapStartOffset=" + telemetryLapStartOffset +
                        ", GapAheadOffset=" + timeGapAheadOffset +
                        ", GapBehindOffset=" + timeGapBehindOffset +
                        ", GapPlaceAheadOffset=" + timeGapPlaceAheadOffset +
                        ", GapPlaceBehindOffset=" + timeGapPlaceBehindOffset +
                        ", VehicleModelOffset=" + vehicleModelOffset +
                        ", LapStartETOffset=" + lapStartETOffset +
                        ", TyreCompoundOffsets=" +
                        tyreCompFLOffset + "," +
                        tyreCompFROffset + "," +
                        tyreCompRLOffset + "," +
                        tyreCompRROffset
                    );
                }

                _lastError = "";
            }
            catch (Exception ex)
            {
                CloseHandles();

                _lastError =
                    ex.GetType().Name +
                    ": " +
                    ex.Message;
            }
        }

        public bool TryReadScoringInfo(
            out LMUScoringInfo info)
        {
            info = new LMUScoringInfo();

            if (!IsConnected)
                return false;

            try
            {
                info =
                    ReadStruct<LMUScoringInfo>(
                        ScoringOffset
                    );

                return true;
            }
            catch (Exception ex)
            {
                LogReadError(ex);
                return false;
            }
        }

        public bool TryReadVehicleScoring(
            int vehicleIndex,
            out LMUVehicleScoring vehicle)
        {
            vehicle = new LMUVehicleScoring();

            if (!IsConnected ||
                vehicleIndex < 0 ||
                vehicleIndex >= MaxVehicles)
            {
                return false;
            }

            try
            {
                long vehicleOffset = ScoringOffset + ExpectedScoringInfoSize + ScoringStreamSize +
                    ((long)vehicleIndex * ExpectedVehicleScoringSize);
                ReadBytes(vehicleOffset, ExpectedVehicleScoringSize);
                vehicle = ReadVehicleScoring(vehicleIndex);

                return true;
            }
            catch (Exception ex)
            {
                LogReadError(ex);
                return false;
            }
        }

        internal bool TryReadVehicleTelemetryRelative(
            int telemetryIndex,
            out LMUVehicleTelemetryRelative telemetry)
        {
            telemetry = new LMUVehicleTelemetryRelative();

            if (!IsConnected ||
                telemetryIndex < 0 ||
                telemetryIndex >= MaxMappedVehicles)
            {
                return false;
            }

            try
            {
                long telemetryOffset =
                    ScoringOffset
                    + ExpectedScoringInfoSize
                    + ScoringStreamSize
                    + ((long)MaxMappedVehicles * ExpectedVehicleScoringSize)
                    + ResultsStreamSize
                    + TelemetryHeaderSize
                    + ((long)telemetryIndex * ExpectedVehicleTelemetryRelativeSize);

                ReadBytes(telemetryOffset, ExpectedVehicleTelemetryRelativeSize);

                byte[] modelBuffer = _telemetryModelBuffers[telemetryIndex];
                if (modelBuffer == null)
                {
                    modelBuffer = new byte[ExpectedVehicleModelSize];
                    _telemetryModelBuffers[telemetryIndex] = modelBuffer;
                }
                Buffer.BlockCopy(
                    _structReadBuffer,
                    ExpectedVehicleModelOffset,
                    modelBuffer,
                    0,
                    ExpectedVehicleModelSize
                );

                telemetry = new LMUVehicleTelemetryRelative
                {
                    mID = BitConverter.ToInt32(_structReadBuffer, ExpectedTelemetryVehicleIdOffset),
                    mLapNumber = BitConverter.ToInt32(_structReadBuffer, ExpectedTelemetryLapNumberOffset),
                    mFuel = BitConverter.ToDouble(_structReadBuffer, ExpectedTelemetryFuelOffset),
                    mLapStartET = BitConverter.ToDouble(_structReadBuffer, ExpectedTelemetryLapStartETOffset),
                    mTrackLimitsSteps = _structReadBuffer[ExpectedTrackLimitsStepsOffset],
                    mTimeGapCarAhead = BitConverter.ToSingle(_structReadBuffer, ExpectedTimeGapCarAheadOffset),
                    mTimeGapCarBehind = BitConverter.ToSingle(_structReadBuffer, ExpectedTimeGapCarBehindOffset),
                    mTimeGapPlaceAhead = BitConverter.ToSingle(_structReadBuffer, ExpectedTimeGapPlaceAheadOffset),
                    mTimeGapPlaceBehind = BitConverter.ToSingle(_structReadBuffer, ExpectedTimeGapPlaceBehindOffset),
                    mVehicleModel = modelBuffer,
                    mTyreCompFL = _structReadBuffer[ExpectedTyreCompFLOffset],
                    mTyreCompFR = _structReadBuffer[ExpectedTyreCompFROffset],
                    mTyreCompRL = _structReadBuffer[ExpectedTyreCompRLOffset],
                    mTyreCompRR = _structReadBuffer[ExpectedTyreCompRROffset],
                    mTyreWearFL = BitConverter.ToDouble(_structReadBuffer, ExpectedTyreWearFLOffset),
                    mTyreWearFR = BitConverter.ToDouble(_structReadBuffer, ExpectedTyreWearFROffset),
                    mTyreWearRL = BitConverter.ToDouble(_structReadBuffer, ExpectedTyreWearRLOffset),
                    mTyreWearRR = BitConverter.ToDouble(_structReadBuffer, ExpectedTyreWearRROffset)
                };

                return true;
            }
            catch (Exception ex)
            {
                LogReadError(ex);
                return false;
            }
        }

        internal bool TryReadTelemetryPlayerIndex(
            out int playerTelemetryIndex,
            out bool playerHasVehicle)
        {
            playerTelemetryIndex = -1;
            playerHasVehicle = false;

            if (!IsConnected)
                return false;

            try
            {
                long telemetryHeaderOffset =
                    ScoringOffset
                    + ExpectedScoringInfoSize
                    + ScoringStreamSize
                    + ((long)MaxMappedVehicles * ExpectedVehicleScoringSize)
                    + ResultsStreamSize;

                ReadBytes(telemetryHeaderOffset, TelemetryHeaderSize);
                playerTelemetryIndex = _structReadBuffer[TelemetryHeaderPlayerIndexOffset];
                playerHasVehicle = _structReadBuffer[TelemetryHeaderPlayerHasVehicleOffset] != 0;

                return playerTelemetryIndex < MaxMappedVehicles;
            }
            catch (Exception ex)
            {
                LogReadError(ex);
                return false;
            }
        }

        private T ReadStruct<T>(long offset)
            where T : struct
        {
            int size =
                Marshal.SizeOf(typeof(T));

            if (_structReadBuffer == null || _structReadBuffer.Length < size)
                _structReadBuffer = new byte[Math.Max(size, ExpectedVehicleTelemetryRelativeSize)];
            if (_structReadPointer == IntPtr.Zero)
                _structReadPointer = Marshal.AllocHGlobal(
                    Math.Max(size, ExpectedVehicleTelemetryRelativeSize)
                );

            ReadBytes(offset, size);

            Marshal.Copy(
                _structReadBuffer,
                0,
                _structReadPointer,
                size
            );

            return (T)Marshal.PtrToStructure(
                _structReadPointer,
                typeof(T)
            );
        }

        private void ReadBytes(long offset, int size)
        {
            if (_structReadBuffer == null || _structReadBuffer.Length < size)
                _structReadBuffer = new byte[Math.Max(size, ExpectedVehicleTelemetryRelativeSize)];

            int bytesRead = _viewAccessor.ReadArray(offset, _structReadBuffer, 0, size);
            if (bytesRead != size)
            {
                throw new InvalidOperationException(
                    "Incomplete memory read: " + bytesRead + " of " + size + " bytes."
                );
            }
        }

        private LMUVehicleScoring ReadVehicleScoring(int vehicleIndex)
        {
            LMUVehicleScoring reusable = _vehicleScoringArrayBuffers[vehicleIndex];
            if (reusable.mDriverName == null)
            {
                reusable.mDriverName = new byte[32];
                reusable.mVehicleName = new byte[64];
                reusable.mVehicleClass = new byte[32];
                reusable.mOri = new LMUVect3[3];
                reusable.mPitGroup = new byte[24];
                reusable.mUpgradePack = new byte[16];
                reusable.mVehFilename = new byte[32];
                reusable.mExpansion = new byte[4];
                _vehicleScoringArrayBuffers[vehicleIndex] = reusable;
            }

            CopyRawArray(VehicleScoringDriverNameOffset, reusable.mDriverName);
            CopyRawArray(VehicleScoringVehicleNameOffset, reusable.mVehicleName);
            CopyRawArray(VehicleScoringVehicleClassOffset, reusable.mVehicleClass);
            CopyRawArray(VehicleScoringPitGroupOffset, reusable.mPitGroup);
            CopyRawArray(VehicleScoringUpgradePackOffset, reusable.mUpgradePack);
            CopyRawArray(VehicleScoringVehicleFileOffset, reusable.mVehFilename);
            CopyRawArray(VehicleScoringExpansionOffset, reusable.mExpansion);

            for (int i = 0; i < reusable.mOri.Length; i++)
            {
                int offset = VehicleScoringOrientationOffset + i * 24;
                reusable.mOri[i] = new LMUVect3
                {
                    x = BitConverter.ToDouble(_structReadBuffer, offset),
                    y = BitConverter.ToDouble(_structReadBuffer, offset + 8),
                    z = BitConverter.ToDouble(_structReadBuffer, offset + 16)
                };
            }

            reusable.mID = BitConverter.ToInt32(_structReadBuffer, VehicleScoringIdOffset);
            reusable.mTotalLaps = BitConverter.ToInt16(_structReadBuffer, VehicleScoringTotalLapsOffset);
            reusable.mSector = unchecked((sbyte)_structReadBuffer[VehicleScoringSectorOffset]);
            reusable.mFinishStatus = unchecked((sbyte)_structReadBuffer[VehicleScoringFinishStatusOffset]);
            reusable.mLapDist = BitConverter.ToDouble(_structReadBuffer, VehicleScoringLapDistOffset);
            reusable.mPathLateral = BitConverter.ToDouble(_structReadBuffer, VehicleScoringPathLateralOffset);
            reusable.mTrackEdge = BitConverter.ToDouble(_structReadBuffer, VehicleScoringTrackEdgeOffset);
            reusable.mBestSector1 = BitConverter.ToDouble(_structReadBuffer, VehicleScoringBestSector1Offset);
            reusable.mBestSector2 = BitConverter.ToDouble(_structReadBuffer, VehicleScoringBestSector2Offset);
            reusable.mBestLapTime = BitConverter.ToDouble(_structReadBuffer, VehicleScoringBestLapTimeOffset);
            reusable.mLastSector1 = BitConverter.ToDouble(_structReadBuffer, VehicleScoringLastSector1Offset);
            reusable.mLastSector2 = BitConverter.ToDouble(_structReadBuffer, VehicleScoringLastSector2Offset);
            reusable.mLastLapTime = BitConverter.ToDouble(_structReadBuffer, VehicleScoringLastLapTimeOffset);
            reusable.mCurSector1 = BitConverter.ToDouble(_structReadBuffer, VehicleScoringCurSector1Offset);
            reusable.mCurSector2 = BitConverter.ToDouble(_structReadBuffer, VehicleScoringCurSector2Offset);
            reusable.mNumPitstops = BitConverter.ToInt16(_structReadBuffer, VehicleScoringNumPitstopsOffset);
            reusable.mNumPenalties = BitConverter.ToInt16(_structReadBuffer, VehicleScoringNumPenaltiesOffset);
            reusable.mIsPlayer = _structReadBuffer[VehicleScoringIsPlayerOffset] != 0;
            reusable.mControl = unchecked((sbyte)_structReadBuffer[VehicleScoringControlOffset]);
            reusable.mInPits = _structReadBuffer[VehicleScoringInPitsOffset] != 0;
            reusable.mPlace = _structReadBuffer[VehicleScoringPlaceOffset];
            reusable.mTimeBehindNext = BitConverter.ToDouble(_structReadBuffer, VehicleScoringTimeBehindNextOffset);
            reusable.mLapsBehindNext = BitConverter.ToInt32(_structReadBuffer, VehicleScoringLapsBehindNextOffset);
            reusable.mTimeBehindLeader = BitConverter.ToDouble(_structReadBuffer, VehicleScoringTimeBehindLeaderOffset);
            reusable.mLapsBehindLeader = BitConverter.ToInt32(_structReadBuffer, VehicleScoringLapsBehindLeaderOffset);
            reusable.mLapStartET = BitConverter.ToDouble(_structReadBuffer, VehicleScoringLapStartETOffset);
            reusable.mPos = ReadVector3(VehicleScoringPositionOffset);
            reusable.mLocalVel = ReadVector3(VehicleScoringLocalVelocityOffset);
            reusable.mLocalAccel = ReadVector3(VehicleScoringLocalAccelerationOffset);
            reusable.mLocalRot = ReadVector3(VehicleScoringLocalRotationOffset);
            reusable.mLocalRotAccel = ReadVector3(VehicleScoringLocalRotationAccelerationOffset);
            reusable.mHeadlights = _structReadBuffer[VehicleScoringHeadlightsOffset];
            reusable.mPitState = _structReadBuffer[VehicleScoringPitStateOffset];
            reusable.mServerScored = _structReadBuffer[VehicleScoringServerScoredOffset];
            reusable.mIndividualPhase = _structReadBuffer[VehicleScoringIndividualPhaseOffset];
            reusable.mQualification = BitConverter.ToInt32(_structReadBuffer, VehicleScoringQualificationOffset);
            reusable.mTimeIntoLap = BitConverter.ToDouble(_structReadBuffer, VehicleScoringTimeIntoLapOffset);
            reusable.mEstimatedLapTime = BitConverter.ToDouble(_structReadBuffer, VehicleScoringEstimatedLapTimeOffset);
            reusable.mFlag = _structReadBuffer[VehicleScoringFlagOffset];
            reusable.mUnderYellow = _structReadBuffer[VehicleScoringUnderYellowOffset] != 0;
            reusable.mCountLapFlag = _structReadBuffer[VehicleScoringCountLapFlagOffset];
            reusable.mInGarageStall = _structReadBuffer[VehicleScoringInGarageStallOffset] != 0;
            reusable.mPitLapDist = BitConverter.ToSingle(_structReadBuffer, VehicleScoringPitLapDistOffset);
            reusable.mBestLapSector1 = BitConverter.ToSingle(_structReadBuffer, VehicleScoringBestLapSector1Offset);
            reusable.mBestLapSector2 = BitConverter.ToSingle(_structReadBuffer, VehicleScoringBestLapSector2Offset);
            reusable.mSteamID = BitConverter.ToUInt64(_structReadBuffer, VehicleScoringSteamIdOffset);
            reusable.mAttackMode = BitConverter.ToInt16(_structReadBuffer, VehicleScoringAttackModeOffset);
            reusable.mFuelFraction = _structReadBuffer[VehicleScoringFuelFractionOffset];
            reusable.mDRSState = _structReadBuffer[VehicleScoringDrsStateOffset] != 0;
            return reusable;
        }

        private void CopyRawArray(int sourceOffset, byte[] destination)
        {
            Buffer.BlockCopy(_structReadBuffer, sourceOffset, destination, 0, destination.Length);
        }

        private LMUVect3 ReadVector3(int offset)
        {
            return new LMUVect3
            {
                x = BitConverter.ToDouble(_structReadBuffer, offset),
                y = BitConverter.ToDouble(_structReadBuffer, offset + 8),
                z = BitConverter.ToDouble(_structReadBuffer, offset + 16)
            };
        }

        private static void ValidateVehicleScoringOffsets(int size)
        {
            if (size != ExpectedVehicleScoringSize ||
                OffsetOf<LMUVehicleScoring>("mID") != VehicleScoringIdOffset ||
                OffsetOf<LMUVehicleScoring>("mDriverName") != VehicleScoringDriverNameOffset ||
                OffsetOf<LMUVehicleScoring>("mVehicleName") != VehicleScoringVehicleNameOffset ||
                OffsetOf<LMUVehicleScoring>("mTotalLaps") != VehicleScoringTotalLapsOffset ||
                OffsetOf<LMUVehicleScoring>("mSector") != VehicleScoringSectorOffset ||
                OffsetOf<LMUVehicleScoring>("mFinishStatus") != VehicleScoringFinishStatusOffset ||
                OffsetOf<LMUVehicleScoring>("mLapDist") != VehicleScoringLapDistOffset ||
                OffsetOf<LMUVehicleScoring>("mPathLateral") != VehicleScoringPathLateralOffset ||
                OffsetOf<LMUVehicleScoring>("mTrackEdge") != VehicleScoringTrackEdgeOffset ||
                OffsetOf<LMUVehicleScoring>("mBestSector1") != VehicleScoringBestSector1Offset ||
                OffsetOf<LMUVehicleScoring>("mBestSector2") != VehicleScoringBestSector2Offset ||
                OffsetOf<LMUVehicleScoring>("mBestLapTime") != VehicleScoringBestLapTimeOffset ||
                OffsetOf<LMUVehicleScoring>("mLastSector1") != VehicleScoringLastSector1Offset ||
                OffsetOf<LMUVehicleScoring>("mLastSector2") != VehicleScoringLastSector2Offset ||
                OffsetOf<LMUVehicleScoring>("mLastLapTime") != VehicleScoringLastLapTimeOffset ||
                OffsetOf<LMUVehicleScoring>("mCurSector1") != VehicleScoringCurSector1Offset ||
                OffsetOf<LMUVehicleScoring>("mCurSector2") != VehicleScoringCurSector2Offset ||
                OffsetOf<LMUVehicleScoring>("mNumPitstops") != VehicleScoringNumPitstopsOffset ||
                OffsetOf<LMUVehicleScoring>("mNumPenalties") != VehicleScoringNumPenaltiesOffset ||
                OffsetOf<LMUVehicleScoring>("mIsPlayer") != VehicleScoringIsPlayerOffset ||
                OffsetOf<LMUVehicleScoring>("mControl") != VehicleScoringControlOffset ||
                OffsetOf<LMUVehicleScoring>("mInPits") != VehicleScoringInPitsOffset ||
                OffsetOf<LMUVehicleScoring>("mPlace") != VehicleScoringPlaceOffset ||
                OffsetOf<LMUVehicleScoring>("mVehicleClass") != VehicleScoringVehicleClassOffset ||
                OffsetOf<LMUVehicleScoring>("mTimeBehindNext") != VehicleScoringTimeBehindNextOffset ||
                OffsetOf<LMUVehicleScoring>("mLapsBehindNext") != VehicleScoringLapsBehindNextOffset ||
                OffsetOf<LMUVehicleScoring>("mTimeBehindLeader") != VehicleScoringTimeBehindLeaderOffset ||
                OffsetOf<LMUVehicleScoring>("mLapsBehindLeader") != VehicleScoringLapsBehindLeaderOffset ||
                OffsetOf<LMUVehicleScoring>("mLapStartET") != VehicleScoringLapStartETOffset ||
                OffsetOf<LMUVehicleScoring>("mPos") != VehicleScoringPositionOffset ||
                OffsetOf<LMUVehicleScoring>("mLocalVel") != VehicleScoringLocalVelocityOffset ||
                OffsetOf<LMUVehicleScoring>("mLocalAccel") != VehicleScoringLocalAccelerationOffset ||
                OffsetOf<LMUVehicleScoring>("mOri") != VehicleScoringOrientationOffset ||
                OffsetOf<LMUVehicleScoring>("mLocalRot") != VehicleScoringLocalRotationOffset ||
                OffsetOf<LMUVehicleScoring>("mLocalRotAccel") != VehicleScoringLocalRotationAccelerationOffset ||
                OffsetOf<LMUVehicleScoring>("mHeadlights") != VehicleScoringHeadlightsOffset ||
                OffsetOf<LMUVehicleScoring>("mPitState") != VehicleScoringPitStateOffset ||
                OffsetOf<LMUVehicleScoring>("mServerScored") != VehicleScoringServerScoredOffset ||
                OffsetOf<LMUVehicleScoring>("mIndividualPhase") != VehicleScoringIndividualPhaseOffset ||
                OffsetOf<LMUVehicleScoring>("mQualification") != VehicleScoringQualificationOffset ||
                OffsetOf<LMUVehicleScoring>("mTimeIntoLap") != VehicleScoringTimeIntoLapOffset ||
                OffsetOf<LMUVehicleScoring>("mEstimatedLapTime") != VehicleScoringEstimatedLapTimeOffset ||
                OffsetOf<LMUVehicleScoring>("mPitGroup") != VehicleScoringPitGroupOffset ||
                OffsetOf<LMUVehicleScoring>("mFlag") != VehicleScoringFlagOffset ||
                OffsetOf<LMUVehicleScoring>("mUnderYellow") != VehicleScoringUnderYellowOffset ||
                OffsetOf<LMUVehicleScoring>("mCountLapFlag") != VehicleScoringCountLapFlagOffset ||
                OffsetOf<LMUVehicleScoring>("mInGarageStall") != VehicleScoringInGarageStallOffset ||
                OffsetOf<LMUVehicleScoring>("mUpgradePack") != VehicleScoringUpgradePackOffset ||
                OffsetOf<LMUVehicleScoring>("mPitLapDist") != VehicleScoringPitLapDistOffset ||
                OffsetOf<LMUVehicleScoring>("mBestLapSector1") != VehicleScoringBestLapSector1Offset ||
                OffsetOf<LMUVehicleScoring>("mBestLapSector2") != VehicleScoringBestLapSector2Offset ||
                OffsetOf<LMUVehicleScoring>("mSteamID") != VehicleScoringSteamIdOffset ||
                OffsetOf<LMUVehicleScoring>("mVehFilename") != VehicleScoringVehicleFileOffset ||
                OffsetOf<LMUVehicleScoring>("mAttackMode") != VehicleScoringAttackModeOffset ||
                OffsetOf<LMUVehicleScoring>("mFuelFraction") != VehicleScoringFuelFractionOffset ||
                OffsetOf<LMUVehicleScoring>("mDRSState") != VehicleScoringDrsStateOffset ||
                OffsetOf<LMUVehicleScoring>("mExpansion") != VehicleScoringExpansionOffset)
            {
                throw new InvalidOperationException("LMU vehicle scoring field offsets do not match the direct reader.");
            }
        }

        private static int OffsetOf<T>(string fieldName)
        {
            return Marshal.OffsetOf(typeof(T), fieldName).ToInt32();
        }

        private void LogReadError(Exception ex)
        {
            _lastError =
                ex.GetType().Name +
                ": " +
                ex.Message;
        }

        private void CloseHandles()
        {
            if (_structReadPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_structReadPointer);
                _structReadPointer = IntPtr.Zero;
            }
            _structReadBuffer = null;

            if (_viewAccessor != null)
            {
                _viewAccessor.Dispose();
                _viewAccessor = null;
            }

            if (_memoryMappedFile != null)
            {
                _memoryMappedFile.Dispose();
                _memoryMappedFile = null;
            }
        }

        public void Dispose()
        {
            CloseHandles();
        }
    }
}

namespace MMOPlugin
{
    [StructLayout(LayoutKind.Explicit, Pack = 4, Size = 1888)]
    internal struct LMUVehicleTelemetryRelative
    {
        [FieldOffset(0)]
        public int mID;

        [FieldOffset(20)]
        public int mLapNumber;

        [FieldOffset(524)]
        public double mFuel;

        [FieldOffset(24)]
        public double mLapStartET;

        [FieldOffset(767)]
        public byte mTrackLimitsSteps;

        [FieldOffset(780)]
        public float mTimeGapCarAhead;

        [FieldOffset(784)]
        public float mTimeGapCarBehind;

        [FieldOffset(788)]
        public float mTimeGapPlaceAhead;

        [FieldOffset(792)]
        public float mTimeGapPlaceBehind;

        [FieldOffset(796)]
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 30)]
        public byte[] mVehicleModel;

        [FieldOffset(1089)]
        public byte mTyreCompFL;

        [FieldOffset(1349)]
        public byte mTyreCompFR;

        [FieldOffset(1609)]
        public byte mTyreCompRL;

        [FieldOffset(1869)]
        public byte mTyreCompRR;

        [FieldOffset(1000)]
        public double mTyreWearFL;

        [FieldOffset(1260)]
        public double mTyreWearFR;

        [FieldOffset(1520)]
        public double mTyreWearRL;

        [FieldOffset(1780)]
        public double mTyreWearRR;
    }
}
