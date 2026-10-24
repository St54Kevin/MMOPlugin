# LMU-Datenkatalog für das MMO Plugin

Stand der Übersicht: Projektversion **1.2.2.6**. Die Liste wurde aus den aktuellen Projektdateien erstellt.

## Umfang und wichtige Einschränkung

Dieser Katalog ist vollständig für die Datenfelder, die das Projekt in seinen C#-Strukturen und im API-Datentyp aktuell **modelliert**. Er ist keine vollständige offizielle Beschreibung jedes Bytes, das LMU im Shared Memory bereitstellt: Teile sind im Projekt absichtlich nicht decodiert und liegen nur als Reserve-/Expansion-Bereiche vor. Die Bedeutungen von Enum- und Rohwerten sollten vor einer Ausgabe im Overlay anhand echter LMU-Werte geprüft werden.

## Datenquellen im Überblick

| Quelle | Zugriff im Projekt | Umfang / Aktualisierung | Verwendung |
|---|---|---|---|
| Scoring Shared Memory `LMU_Data` | Memory-mapped file; Scoring beginnt bei Offset 1632 | `LMUScoringInfo` 548 Byte; bis 62 Scoring-Fahrzeuge mit je 584 Byte | Sitzung, Streckenlänge, Fahrzeuge, Positionen, Zeitwerte und Fahrzeugzustände |
| Telemetrie im selben Shared Memory | eigener Bereich nach Scoring- und Results-Bereich | 104 Einträge mit je 1888 Byte plus 4-Byte-Header | Track-Limit-Schritte, Reifenmischungen und relative Zeitlücken; im Plugin per Fahrzeug-ID zugeordnet |
| LMU Standings API | `http://127.0.0.1:6397/rest/watch/standings` | JSON-Array, Abfrage alle 250 ms, maximal 62 Positionen | Namen, Klassen, Zeiten, Abstände, Pit-/Energie-/Strafdaten und Finish-Status |

Der Shared-Memory-Leser prüft beim Verbindungsaufbau Strukturgrößen und ausgewählte Telemetrie-Offsets. Der 64-KiB-Results-Bereich wird beim Berechnen der Telemetrieposition übersprungen, aber derzeit nicht als Ergebnisdaten interpretiert.

## 1. Scoring-Header: `LMUScoringInfo`

Alle folgenden Felder sind im Projekt als `LMUScoringInfo` modelliert. Direkt in Properties verwendet werden aktuell `mTrackName`, `mSession`, `mNumVehicles` und `mLapDist`; die übrigen Felder sind mögliche Feature-Quellen, aber noch nicht als Properties verfügbar.

| Feld | C#-Typ | Inhalt / mögliche Nutzung |
|---|---|---|
| `mTrackName` | `byte[64]` | Streckenname; wird bereits als `MMO_TrackName` decodiert |
| `mSession` | `int` | Sitzungskennung; wird intern zum Unterscheiden von Training/Qualifying/Rennen genutzt |
| `mCurrentET` | `double` | Aktuelle verstrichene Sitzungszeit (ET) |
| `mEndET` | `double` | Sitzungs-Endzeit als ET, soweit von LMU gesetzt |
| `mMaxLaps` | `int` | Vorgesehene Rundenzahl |
| `mLapDist` | `double` | Streckenlänge; intern für Relative-Abstände verwendet |
| `mResultsStreamPointer` | `byte[8]` | Pointer-/Handle-Rohdaten zum Results-Bereich; wird nicht dereferenziert |
| `mNumVehicles` | `int` | Anzahl gemeldeter Fahrzeuge; wird als `MMO_VehicleCount` bereitgestellt |
| `mGamePhase` | `byte` | Spiel-/Sessionphase, Rohwert/Enum nicht decodiert |
| `mYellowFlagState` | `sbyte` | Globaler Gelbflaggenstatus, Rohwert/Enum nicht decodiert |
| `mSectorFlag` | `byte[3]` | Flaggenstatus der drei Streckensektoren, Rohwerte |
| `mStartLight` | `byte` | Startampelstatus, Rohwert |
| `mNumRedLights` | `byte` | Anzahl roter Startlichter |
| `mInRealtime` | `bool` | Echtzeit-/Livezustand |
| `mPlayerName` | `byte[32]` | Fahrername des Spielers aus dem Shared Memory |
| `mPlrFileName` | `byte[64]` | Spieler-/Profil-Dateiname |
| `mDarkCloud` | `double` | Bewölkungswert; Skalierung im Spiel prüfen |
| `mRaining` | `double` | Regenwert/-intensität; Skalierung im Spiel prüfen |
| `mAmbientTemp` | `double` | Umgebungstemperatur |
| `mTrackTemp` | `double` | Streckentemperatur |
| `mWind` | `LMUVect3` | Windvektor mit `x`, `y`, `z`; Einheiten/Komponentenbezug prüfen |
| `mMinPathWetness` | `double` | Minimale Nässe des Fahrbahnpfads |
| `mMaxPathWetness` | `double` | Maximale Nässe des Fahrbahnpfads |
| `mGameMode` | `byte` | Spielmodus, Rohwert/Enum nicht decodiert |
| `mIsPasswordProtected` | `bool` | Server passwortgeschützt |
| `mServerPort` | `ushort` | Server-Port |
| `mServerPublicIP` | `uint` | Öffentliche Server-IP als numerischer Rohwert |
| `mMaxPlayers` | `int` | Maximale Spielerzahl des Servers |
| `mServerName` | `byte[32]` | Servername |
| `mStartET` | `float` | Startzeit der Sitzung als ET |
| `mAvgPathWetness` | `double` | Durchschnittliche Fahrbahnnässe |
| `mSessionTimeRemaining` | `float` | Verbleibende Sitzungszeit |
| `mTimeOfDay` | `float` | Tageszeit; Format/Skalierung prüfen |
| `mIsFixedSetup` | `bool` | Fixed-Setup-Regel aktiv |
| `mTrackGripLevel` | `byte` | Grip-Level, Rohwert/Skala prüfen |
| `mCloudCoverage` | `byte` | Bewölkungsstufe, Rohwert/Skala prüfen |
| `mTrackLimitsStepsPerPenalty` | `byte` | Track-Limit-Schritte bis zu einer Strafe |
| `mTrackLimitsStepsPerPoint` | `byte` | Track-Limit-Schritte pro Punkt/Einheit |
| `mExpansion` | `byte[187]` | Nicht dekodierter Erweiterungsbereich |
| `mVehiclePointer` | `byte[8]` | Pointer-/Handle-Rohdaten; wird nicht dereferenziert |

## 2. Fahrzeug-Scoring: `LMUVehicleScoring`

Es werden bis zu 62 Datensätze gelesen. Aktuell greift das Plugin direkt auf `mID`, `mPlace`, `mIsPlayer`, `mLastLapTime`, `mLapStartET`, `mLapsBehindNext`, `mLapsBehindLeader`, `mLapDist`, `mInPits` und `mInGarageStall` zu. Weitere Felder stehen im Datensatz bereit, sind aber noch nicht als Plugin-Properties umgesetzt.

| Feld | C#-Typ | Inhalt / mögliche Nutzung |
|---|---|---|
| `mID` | `int` | Fahrzeug-ID; verbindet Scoring- und Telemetrieeintrag |
| `mDriverName` | `byte[32]` | Fahrername aus Shared Memory; das Plugin nutzt derzeit den Namen der API |
| `mVehicleName` | `byte[64]` | Fahrzeug-/Modellname |
| `mTotalLaps` | `short` | Gesamtzahl absolvierter Runden |
| `mSector` | `sbyte` | Aktueller Sektor, Rohwert |
| `mFinishStatus` | `sbyte` | Finish-Status, Rohwert/Enum |
| `mLapDist` | `double` | Position entlang der Runde; intern für Relative-Abstände verwendet |
| `mPathLateral` | `double` | Seitlicher Abstand zur Ideallinie/Pfad |
| `mTrackEdge` | `double` | Abstand zur Streckenkante |
| `mBestSector1` | `double` | Beste Sektorzeit 1 |
| `mBestSector2` | `double` | Beste Sektorzeit 2 |
| `mBestLapTime` | `double` | Beste Rundenzeit im Scoring-Datensatz |
| `mLastSector1` | `double` | Letzte Sektorzeit 1 |
| `mLastSector2` | `double` | Letzte Sektorzeit 2 |
| `mLastLapTime` | `double` | Letzte Rundenzeit; negativer Wert wird im Plugin als invalid erkannt |
| `mCurSector1` | `double` | Laufende Sektorzeit 1 |
| `mCurSector2` | `double` | Laufende Sektorzeit 2 |
| `mNumPitstops` | `short` | Anzahl Boxenstopps |
| `mNumPenalties` | `short` | Anzahl Strafen laut Scoring |
| `mIsPlayer` | `bool` | Kennzeichnet das Spielerfahrzeug |
| `mControl` | `sbyte` | Steuerungsart, Rohwert/Enum |
| `mInPits` | `bool` | Fahrzeug in der Box |
| `mPlace` | `byte` | Gesamtposition; bestimmt die Property-Positionsnummer |
| `mVehicleClass` | `byte[32]` | Fahrzeugklasse aus Shared Memory |
| `mTimeBehindNext` | `double` | Zeitabstand zum nächsten Fahrzeug laut Scoring |
| `mLapsBehindNext` | `int` | Rundendifferenz zum nächsten Fahrzeug; intern für Renn-Intervalle genutzt |
| `mTimeBehindLeader` | `double` | Zeitabstand zum Führenden laut Scoring |
| `mLapsBehindLeader` | `int` | Rundendifferenz zum Führenden; intern für Renn-Gap genutzt |
| `mLapStartET` | `double` | ET-Zeitpunkt des Rundenstarts; intern zur Berechnung der letzten Rundenzeit gespeichert |
| `mPos` | `LMUVect3` | Weltposition des Fahrzeugs |
| `mLocalVel` | `LMUVect3` | Lokale Geschwindigkeit |
| `mLocalAccel` | `LMUVect3` | Lokale Beschleunigung |
| `mOri` | `LMUVect3[3]` | Orientierung als drei Vektoren |
| `mLocalRot` | `LMUVect3` | Lokale Rotation/Rotationsgeschwindigkeit; genaue Semantik prüfen |
| `mLocalRotAccel` | `LMUVect3` | Lokale Rotationsbeschleunigung |
| `mHeadlights` | `byte` | Scheinwerferstatus |
| `mPitState` | `byte` | Boxenstatus als Rohwert |
| `mServerScored` | `byte` | Server-Scoring-Status/Rohwert |
| `mIndividualPhase` | `byte` | Individuelle Fahrzeugphase/Rohwert |
| `mQualification` | `int` | Qualifikations-/Scoring-Status, Bedeutung des Rohwerts prüfen |
| `mTimeIntoLap` | `double` | Zeit seit Beginn der laufenden Runde |
| `mEstimatedLapTime` | `double` | Geschätzte Zeit der laufenden Runde |
| `mPitGroup` | `byte[24]` | Pit-/Teamgruppe als Text/Rohdaten |
| `mFlag` | `byte` | Fahrzeugflagge, Rohwert |
| `mUnderYellow` | `bool` | Fahrzeug unter Gelb |
| `mCountLapFlag` | `byte` | Rundenzähl-Flag/Rohwert |
| `mInGarageStall` | `bool` | Fahrzeug im Garagenplatz; intern für Relative-Auswahl berücksichtigt |
| `mUpgradePack` | `byte[16]` | Upgrade-Paketkennung/Rohdaten |
| `mPitLapDist` | `float` | Position/Distanz im Boxenbereich |
| `mBestLapSector1` | `float` | Sektor-1-Bestzeit, alternative Scoring-Kopie |
| `mBestLapSector2` | `float` | Sektor-2-Bestzeit, alternative Scoring-Kopie |
| `mSteamID` | `ulong` | Steam-ID als Zahl |
| `mVehFilename` | `byte[32]` | Fahrzeug-Dateiname |
| `mAttackMode` | `short` | Attack-Mode-Wert/Rohwert |
| `mFuelFraction` | `byte` | Kraftstoffanteil als Rohwert; API stellt zusätzlich `fuelFraction` als `double` bereit |
| `mDRSState` | `bool` | DRS-Status |
| `mExpansion` | `byte[4]` | Nicht dekodierter Erweiterungsbereich |

`LMUVect3` besteht aus drei `double`-Feldern: `x`, `y`, `z`. Das gilt für Position, Geschwindigkeit, Beschleunigung, Orientierung und Wind.

## 3. Relativ-Telemetrie: `LMUVehicleTelemetryRelative`

Die Telemetriestruktur ist 1888 Byte groß. Nur die folgenden Felder sind im Projekt benannt; die übrigen Bytebereiche zwischen den Offsets sind nicht decodiert.

| Feld | Offset | Typ | Status / mögliche Nutzung |
|---|---:|---|---|
| `mID` | 0 | `int` | Verknüpft Telemetrie mit Scoring-Fahrzeug |
| `mLapStartET` | 24 | `double` | Rundenstartzeit; derzeit nicht für die Berechnung verwendet (die Berechnung nimmt `LMUVehicleScoring.mLapStartET`) |
| `mTrackLimitsSteps` | 776 | `byte` | Wird als Incident-/Track-Limit-Zähler pro Fahrzeug und Spieler verwendet |
| `mTimeGapCarAhead` | 780 | `float` | Zeitabstand zum Auto vor diesem Fahrzeug; interne Relative-Werte |
| `mTimeGapCarBehind` | 784 | `float` | Zeitabstand zum Auto hinter diesem Fahrzeug; interne Relative-Werte |
| `mTimeGapPlaceAhead` | 788 | `float` | Zeitabstand zur Platzierung davor; verfügbar, Verwendung im Relative-Fallback prüfen |
| `mTimeGapPlaceBehind` | 792 | `float` | Zeitabstand zur Platzierung dahinter; verfügbar, Verwendung im Relative-Fallback prüfen |
| `mTyreCompFL` | 1089 | `byte` | Reifenmischung vorne links; Mapping 0 Soft, 1 Medium, 2 Hard, 3 Wet |
| `mTyreCompFR` | 1349 | `byte` | Reifenmischung vorne rechts |
| `mTyreCompRL` | 1609 | `byte` | Reifenmischung hinten links |
| `mTyreCompRR` | 1869 | `byte` | Reifenmischung hinten rechts |

Die Abstände 4–23, 32–775, 796–1088, 1090–1348, 1350–1608 und 1610–1868 sind im aktuellen Projekt nicht benannte Bereiche. Ohne passende LMU-Strukturdefinition sollte daraus keine Bedeutung abgeleitet werden.

## 4. Standings-API: `LMUApiVehicle`

Die API-Antwort wird als JSON-Array gelesen und nach `position` in ein Array mit 62 Plätzen sortiert. Nicht in `LMUApiVehicle` definierte JSON-Felder werden vom aktuellen Projekt nicht ausgewertet.

| JSON-Feld | C#-Property | Typ | Status / mögliche Nutzung |
|---|---|---|---|
| `position` | `Position` | `int` | API-Gesamtposition; Schlüssel für Positionszuordnung |
| `carNumber` | `CarNumber` | `string` | Startnummer; wird als `CarNumber` ausgegeben |
| `carClass` | `CarClass` | `string` | Fahrzeugklasse; wird als `CarClass` ausgegeben und zum Gruppieren verwendet |
| `driverName` | `DriverName` | `string` | Fahrername; Quelle für `DriverName` und `DriveNameRelative` |
| `fullTeamName` | `TeamName` | `string` | Teamname; Quelle für `TeamName` und optionale Namensformate |
| `bestLapTime` | `BestLapTime` | `double` | Beste Rundenzeit in Sekunden; wird ausgegeben |
| `lastLapTime` | `LastLapTime` | `double` | API-letzte Rundenzeit in Sekunden; Fallback, wenn Shared-Memory-Werte fehlen |
| `timeBehindLeader` | `TimeBehindLeader` | `double` | API-Zeitabstand zum Gesamtführenden; im aktuellen Property-Mapping nicht direkt verwendet |
| `timeBehindNext` | `TimeBehindNext` | `double` | API-Zeitabstand zum nächsten Fahrzeug; Renn-Interval-Quelle |
| `timeBehindClassLeader` | `TimeBehindClassLeader` | `double` | API-Zeitabstand zum Klassenführenden; Renn-Gap-Quelle |
| `pitState` | `PitState` | `string` | Textlicher Boxenstatus; wird zur `PitStatus`-Ausgabe ausgewertet |
| `pitting` | `Pitting` | `bool` | Fahrzeug ist beim Boxenstopp; `PitStatus`-Auswertung |
| `inGarageStall` | `InGarageStall` | `bool` | Fahrzeug im Garagenplatz; `PitStatus`-Auswertung |
| `veFraction` | `VeFraction` | `double` | Virtual-Energy-Anteil; Eingabe für `VirtualEnergy` |
| `fuelFraction` | `FuelFraction` | `double` | Kraftstoffanteil; Ersatzquelle für `VirtualEnergy` |
| `penalties` | `Penalties` | `int` | Strafenzahl; wird ausgegeben |
| `finishStatus` | `FinishStatus` | `string` | Finish-Status; wird zu `FinishStatus` 0/1 normalisiert |

Zusätzlich berechnet das Plugin intern API-Felder:

| Property | Berechnung |
|---|---|
| `PositionClass` | Im Training/Qualifying aus der Reihenfolge gültiger Bestzeiten innerhalb der Klasse; im Rennen aus der Gesamtpositionsreihenfolge je Klasse |
| `PracticeQualifyingGap` | Bestzeit minus Klassenführungs-Bestzeit; in Rennen als nicht verfügbar markiert |
| `PracticeQualifyingInterval` | Bestzeit minus nächstschnellster Bestzeit derselben Klasse; in Rennen als nicht verfügbar markiert |
| `GapToLeader` | Training/Qualifying: `PracticeQualifyingGap`; Rennen: `TimeBehindClassLeader` |
| `IntervalToNext` | Training/Qualifying: `PracticeQualifyingInterval`; Rennen: `TimeBehindNext` |

## 5. Bereits verfügbare Plugin-Properties

### Globale Properties

`MMO_ConnectionStatus`, `MMO_TrackName`, `MMO_VehicleCount`, `MMO_ApiConnectionStatus`, `MMO_ApiLastError`, `MMO_ShowTeamNamesInLeaderboard`, `MMO_AdditionalTeamNamesInRelative`, `MMO_InvalidLap`, `MMO_PlayerInc`, `MMO_maxIncidents`, `MMO_RelativeAhead1`–`3`, `MMO_RelativeBehind1`–`3`.

### Je Position 1 bis 62

`MMO_PositionN` + eines dieser Suffixe:

`Overall`, `Class`, `CarNumber`, `CarClass`, `TyreCompFL`, `TyreCompFR`, `TyreCompRL`, `TyreCompRR`, `DriverName`, `DriveNameRelative`, `TeamName`, `BestLaptime`, `LastLaptime`, `GapToLeader`, `IntervalToNext`, `PitStatus`, `VirtualEnergy`, `Penalties`, `Incidents`, `InvalidLap`, `FinishStatus`.

Das ergibt aktuell **16 globale Properties + 62 × 21 Positions-Properties = 1318 Properties**. Manche Felder sind im Menü leer oder 0, wenn die jeweilige Quelle gerade keinen Wert liefert.

## 6. Noch nicht als Properties erschlossene Feature-Kandidaten

Aus den bereits modellierten Quellen lassen sich besonders folgende Ideen prüfen:

- **Session & Flaggen:** Spielphase, globale Gelbflagge, Sektorflaggen, Startampel, verbleibende Zeit und maximale Rundenzahl.
- **Wetter & Strecke:** Regen, Wolken, Umgebung-/Streckentemperatur, Wind, Fahrbahnnässe und Grip.
- **Runden-/Sektorzeiten:** letzte und beste Sektorzeiten, aktuelle Sektorzeiten, Zeit in Runde und geschätzte Rundenzeit.
- **Autozustand:** Position, Geschwindigkeit, Beschleunigung, Orientierung, Abstand zur Ideallinie und Streckenkante.
- **Boxen & Rennzustand:** Boxenstopps, Pit-Lap-Distanz, Flaggen am Fahrzeug, unter Gelb, Attack Mode und DRS.
- **Relative-Positionen:** `mTimeGapPlaceAhead` und `mTimeGapPlaceBehind` gezielt gegen die vorhandenen Fahrzeug- und API-Gaps prüfen.
- **Spieler-/Serverinformationen:** Spielerprofilname, Servername, Servermodus, Passwortschutz, Spielerlimit, Fixed Setup und Server-IP/-Port.

Vor einer Umsetzung sollte jeweils im Spiel überprüft werden, ob der Wert live gefüllt wird, welche Einheiten/Enums gelten und ob er im Training, Qualifying und Rennen gleichermaßen sinnvoll ist.

## Verwendete Projektdateien

- `LMUScoringInfo.cs`
- `LMUVehicleScoring.cs`
- `LMUSharedMemory.cs`
- `LMUStandingsApi.cs`
- `MMOPlugin.cs.cs`
