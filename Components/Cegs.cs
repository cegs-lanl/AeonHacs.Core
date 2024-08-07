﻿using AeonHacs.Utilities;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using static AeonHacs.Components.CegsPreferences;
using static AeonHacs.Utilities.Utility;

namespace AeonHacs.Components
{
    public class Cegs : ProcessManager, ICegs
    {
        #region HacsComponent

        [HacsConnect]
        protected virtual void Connect()
        {
            InletPort = Find<InletPort>(inletPortName);
        }


        /// <summary>
        /// Notify the operator if the required object is missing.
        /// </summary>
        private void CegsNeeds(object obj, string objName)
        {
            if (obj == null)
                Warn("Configuration Error",
                    $"Can't find {objName}. Cegs needs one Connected.");
        }


        [HacsPostConnect]
        protected virtual void PostConnect()
        {
            // check that the essentials are found
            //CegsNeeds(Power, nameof(Power));
            CegsNeeds(Ambient, nameof(Ambient));
            CegsNeeds(VacuumSystem1, nameof(VacuumSystem1));
            CegsNeeds(IM, nameof(IM));
            CegsNeeds(VTT, nameof(VTT));
            CegsNeeds(MC, nameof(MC));
            CegsNeeds(Split, nameof(Split));
            CegsNeeds(GM, nameof(GM));
            CegsNeeds(VTT_MC, nameof(VTT_MC));
            CegsNeeds(MC_Split, nameof(MC_Split));
            CegsNeeds(ugCinMC, nameof(ugCinMC));

            foreach (var cf in Coldfingers.Values)
                cf.SlowToFreeze += OnSlowToFreeze;

            if (Power is Power p)
            {
                p.MainsDown += OnMainsDown;
                p.MainsFailed += OnMainsFailed;
                p.MainsRestored += OnMainsRestored;
            }

            foreach (var x in LNManifolds.Values)
            {
                x.OverflowDetected += OnOverflowDetected;
                x.SlowToFill += OnSlowToFill;
            }

            //ugCinMC depends on both of these, but they are both updated
            //every daq cycle so only one needs to trigger the update
            //MC.Manometer.PropertyChanged += UpdateSampleMeasurement;
            MC.Thermometer.PropertyChanged += UpdateSampleMeasurement;
            MaximumAliquotsPerSample = 1 + MC.Ports?.Count ?? 0;

            foreach (var c in VolumeCalibrations.Values)
            {
                c.ProcessStep = ProcessStep;
                c.ProcessSubStep = ProcessSubStep;
                c.OpenLine = OpenLine;
                c.OkPressure = OkPressure;
                c.Log = TestLog;
            }
        }

        [HacsPostStart]
        protected virtual void PostStart()
        {
            Stopping = false;
            SystemUpTime.Start();
            StartThreads();
            Started = true;
            SaveSettingsToFile("startup.json");
        }


        [HacsPreStop]
        protected virtual void PreStop()
        {
            try
            {
                EventLog.Record("System shutting down");
                Stopping = true;

                lowPrioritySignal.Set();
                stoppedSignal2.WaitOne();

                // Note: controllers of multiple devices should shutdown in Stop()
                // The devices they control should have their shutdown states
                // effected in PreStop()

            }
            catch (Exception e) { Notice.Send(e.ToString()); }
        }


        protected bool StopLogging { get; set; } = false;


        [HacsPostStop]
        protected virtual void PostStop()
        {
            try
            {
                UpdateTimer?.Dispose();

                StopLogging = true;
                SystemLogSignal.Set();
                stoppedSignal1.WaitOne();

                SaveSettings();

                SerialPortMonitor.Stop();
            }
            catch (Exception e) { Notice.Send(e.ToString()); }
        }

        /// <summary>
        /// system status logs stopped
        /// </summary>
        ManualResetEvent stoppedSignal1 = new ManualResetEvent(true);

        /// <summary>
        /// low priority activities stopped
        /// </summary>
        ManualResetEvent stoppedSignal2 = new ManualResetEvent(true);
        public new bool Stopped => stoppedSignal1.WaitOne(0) && stoppedSignal2.WaitOne(0);
        protected bool Stopping { get; set; }

        #endregion HacsComponent

        #region System configuration

        #region Component lists
        [JsonProperty] public Dictionary<string, IDeviceManager> DeviceManagers { get; set; }
        [JsonProperty] public Dictionary<string, IManagedDevice> ManagedDevices { get; set; }
        [JsonProperty] public Dictionary<string, IMeter> Meters { get; set; }
        [JsonProperty] public Dictionary<string, IValve> Valves { get; set; }
        [JsonProperty] public Dictionary<string, ISwitch> Switches { get; set; }
        [JsonProperty] public Dictionary<string, IHeater> Heaters { get; set; }
        [JsonProperty] public Dictionary<string, IPidSetup> PidSetups { get; set; }

        [JsonProperty] public Dictionary<string, ILNManifold> LNManifolds { get; set; }
        [JsonProperty] public Dictionary<string, IColdfinger> Coldfingers { get; set; }
        [JsonProperty] public Dictionary<string, IVTColdfinger> VTColdfingers { get; set; }

        [JsonProperty] public Dictionary<string, IVacuumSystem> VacuumSystems { get; set; }
        [JsonProperty] public Dictionary<string, IChamber> Chambers { get; set; }
        [JsonProperty] public Dictionary<string, ISection> Sections { get; set; }
        [JsonProperty] public Dictionary<string, IGasSupply> GasSupplies { get; set; }
        [JsonProperty] public Dictionary<string, IFlowManager> FlowManagers { get; set; }
        [JsonProperty] public Dictionary<string, IVolumeCalibration> VolumeCalibrations { get; set; }
        [JsonProperty] public Dictionary<string, IHacsLog> Logs { get; set; }

        // The purpose of the FindAll().ToDictionary is to automate deletions
        // from the settings file (i.e., to avoid needing a backing variable and
        // Samples.Remove())
        [JsonProperty]
        public Dictionary<string, Sample> Samples
        {
            get => FindAll<Sample>().ToDictionary(s => s.Name, s => s);
            set { }
        }

        [JsonProperty] public Dictionary<string, IHacsComponent> HacsComponents { get; set; }

        #endregion Component lists

        #region HacsComponents
        [JsonProperty] public virtual Power Power { get; set; }

        #region Data Logs
        public virtual DataLog AmbientLog { get; set; }
        public virtual DataLog VM1PressureLog { get; set; }
        public virtual HacsLog SampleLog { get; set; }
        public virtual HacsLog TestLog { get; set; }
        #endregion Data Logs

        public virtual IChamber Ambient { get; set; }
        public virtual IVacuumSystem VacuumSystem1 { get; set; }

        public virtual ISection IM { get; set; }
        public virtual ISection CT { get; set; }
        public virtual ISection VTT { get; set; }
        public virtual ISection MC { get; set; }
        public virtual ISection Split { get; set; }
        public virtual ISection d13C { get; set; }
        public virtual ISection d13C_14C { get; set; }
        public virtual ISection GM { get; set; }

        public virtual ISection d13CM { get; set; }
        public virtual ISection VTT_MC { get; set; }
        public virtual ISection MC_Split { get; set; }

        // insist on an actual Meter, to enable implicit double
        public virtual Meter ugCinMC { get; set; }

        /// <summary>
        /// The sample gas collection path.
        /// </summary>
        protected virtual ISection IM_FirstTrap
        { 
            get
            {
                if (im_FirstTrap != null) return im_FirstTrap;
                if (!IpIm(out ISection im)) return null;

                var trap = CT ?? VTT;
                if (trap == null)
                {
                    Warn("Configuration error", $"Can't find first trap");
                    return null;
                }

                var firstChamber = im.Chambers?.First();
                var lastChamber = trap.Chambers?.Last();
                var im_trap = FirstOrDefault<Section>(s =>
                    s.Chambers?.First() == firstChamber &&
                    s.Chambers?.Last() == lastChamber);

                if (im_trap == null)
                    Warn("Configuration error", $"Can't find Section linking {im.Name} and {trap.Name}");
                return im_trap;
            }
            set => Ensure(ref im_FirstTrap, value);
        }
        ISection im_FirstTrap = null;

        /// <summary>
        /// The Section containing only one chamber, the first trap.
        /// </summary>
        protected virtual ISection FirstTrap
        {
            get
            {
                var chamber = IM_FirstTrap?.Chambers?.Last();
                if (chamber == null)
                {
                    Warn("Configuration error", $"Can't find IM_FirstTrap Section.");
                    return null;
                }
                var trap = FirstOrDefault<Section>(s =>
                   s.Chambers?.First() == chamber &&
                   s.Chambers.Count() == 1);
                if (trap == null)
                {
                    Warn("Configuration error", $"Can't find a Section containing only chamber {chamber.Name}.");
                }
                return trap;
            }
        }

        #endregion HacsComponents

        #region Constants

        [JsonProperty]
        public virtual CegsPreferences CegsPreferences { get; set; }

        #region Globals

        #region UI Communications

        public virtual Func<bool, List<ISample>> SelectSamples { get; set; }

        public virtual void PlaySound() => Notice.Send("PlaySound", Notice.Type.Tell);

        #endregion UI Communications

        public override bool Busy
        {
            get => base.Busy || !Free;
            protected set => base.Busy = value;
        }

        public bool Free
        {
            get => free;
            protected set
            {
                if (Ensure(ref free, value))
                {
                    NotifyPropertyChanged(nameof(Busy));
                    NotifyPropertyChanged(nameof(NotBusy));
                }
            }
        }
        bool free = true;

        [JsonProperty]
        public bool AutoFeedEnabled
        {
            get => autoFeedEnabled;
            set => Ensure(ref autoFeedEnabled, value);
        }
        bool autoFeedEnabled = false;

        public virtual string PriorAlertMessage => AlertManager.PriorAlertMessage;

        public virtual List<IInletPort> InletPorts { get; set; }
        public virtual List<IGraphiteReactor> GraphiteReactors { get; set; }
        public virtual List<Id13CPort> d13CPorts { get; set; }


        [JsonProperty("InletPort")]
        string InletPortName { get => InletPort?.Name; set => inletPortName = value; }
        string inletPortName;
        public virtual IInletPort InletPort
        {
            get => inletPort;
            set
            {
                Ensure(ref inletPort, value, OnPropertyChanged);
                if (InletPort?.Sample is ISample sample)
                    Sample = sample;
            }
        }
        IInletPort inletPort;

        protected void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (sender == InletPort && InletPort.Sample is ISample sample)
                Sample = sample;
        }

        public virtual ISample Sample
        {
            get => sample;
            set => Ensure(ref sample, value);
        }
        ISample sample;

        protected virtual Id13CPort d13CPort
        {
            get => _d13CPort ?? Guess_d13CPort();
            set => _d13CPort = value;
        }
        Id13CPort _d13CPort;

        protected virtual Id13CPort Guess_d13CPort() =>
            Guess_d13CPort(Sample);

        protected virtual Id13CPort Guess_d13CPort(ISample sample) =>
            Guess_d13CPort(sample?.InletPort);

        protected virtual Id13CPort Guess_d13CPort(IInletPort inletPort)
        {
            if (inletPort?.Name is string ipName &&
                ipName.StartsWith("IP") &&
                ipName.Length > 2 &&
                FirstOrDefault<Id13CPort>(p => p.Name.EndsWith(ipName[2..]) && !p.ShouldBeClosed) is Id13CPort p)
                return p;
            return FirstOrDefault<Id13CPort>(p => !p.ShouldBeClosed);
        }

        #endregion Globals



        #endregion Constants

        #endregion System configuration

        #region System elements not saved/restored in Settings

        public new virtual bool Started { get; protected set; }

        protected Action DuringBleed;

        #region Threading

        public Timer UpdateTimer { get; set; }

        // logging
        Thread systemLogThread;
        protected AutoResetEvent SystemLogSignal { get; private set; } = new AutoResetEvent(false);

        // low priority activity
        Thread lowPriorityThread;
        protected AutoResetEvent lowPrioritySignal { get; private set; } = new AutoResetEvent(false);

        #endregion Threading

        // system conditions
        protected Stopwatch SystemUpTime { get; private set; } = new Stopwatch();
        public virtual TimeSpan Uptime => SystemUpTime.Elapsed;

        // process management
        public virtual bool SampleIsRunning => ProcessSequenceIsRunning;

        #endregion System elements not saved in/restored from Settings

        #region Startup and ShutDown

        protected virtual void StartThreads()
        {
            EventLog.Record("System Started");

            systemLogThread = new Thread(LogSystemStatus)
            {
                Name = $"{Name} logSystemStatus",
                IsBackground = true
            };
            systemLogThread.Start();

            lowPriorityThread = new Thread(LowPriorityActivities)
            {
                Name = $"{Name} lowPriorityActivities",
                IsBackground = true
            };
            lowPriorityThread.Start();

            UpdateTimer = new Timer(UpdateTimerCallback, null, 0, UpdateIntervalMilliseconds);
        }

        #endregion Startup and ShutDown

        #region elementary utility functions

        /// <summary>
        /// From (pv = nkt): n = pv / kt
        /// </summary>
        /// <param name="pressure">Torr</param>
        /// <param name="volume">milliliters</param>
        /// <param name="temperature">°C</param>
        /// <returns>number of particles</returns>
        protected virtual double Particles(double pressure, double volume, double temperature) =>
            pressure * volume / BoltzmannConstantTorr_mL / (ZeroDegreesC + temperature);

        /// <summary>
        /// Temperature-dependent pressure for number of particles in a fixed volume (in milliliters).
        /// </summary>
        /// <param name="particles">number of particles</param>
        /// <param name="volume">milliliters</param>
        /// <returns></returns>
        protected virtual double TorrPerKelvin(double particles, double volume) =>
            particles * BoltzmannConstantTorr_mL / volume;

        /// <summary>
        /// From (pv = nkt): p = nkt / v.
        /// Units are Torr, K, milliliters
        /// </summary>
        /// <param name="particles">number of particles</param>
        /// <param name="volume">milliliters</param>
        /// <param name="temperature">°C</param>
        /// <returns>pressure in Torr</returns>
        protected virtual double Pressure(double particles, double volume, double temperature) =>
            (ZeroDegreesC + temperature) * TorrPerKelvin(particles, volume);

        /// <summary>
        /// The mass of carbon in a quantity of CO2 gas, given its pressure, volume and temperature.
        /// </summary>
        /// <param name="pressure">Torr</param>
        /// <param name="volume">mL</param>
        /// <param name="temperature">°C</param>
        /// <returns></returns>
        protected virtual double MicrogramsCarbon(double pressure, double volume, double temperature) =>
            Particles(pressure, volume, temperature) / CarbonAtomsPerMicrogram;

        /// <summary>
        /// The mass of carbon in the chamber, assuming it contains only gaseous CO2.
        /// </summary>
        /// <param name="ch"></param>
        /// <returns></returns>
        protected virtual double MicrogramsCarbon(IChamber ch) =>
            MicrogramsCarbon(ch.Pressure, ch.MilliLiters, ch.Temperature);

        /// <summary>
        /// The mass of carbon in the section, assuming it contains only gaseous CO2.
        /// </summary>
        /// <param name="section"></param>
        /// <returns></returns>
        protected virtual double MicrogramsCarbon(ISection section) =>
            MicrogramsCarbon(section.Pressure, section.CurrentVolume(true), section.Temperature);

        #endregion elementary utility functions

        #region Periodic system activities & maintenance

        #region Logging
        protected virtual void LogSystemStatus()
        {
            stoppedSignal1.Reset();
            try
            {
                while (!StopLogging)
                {
                    if (!Started) continue;
                    try { HacsLog.UpdateAll(); }
                    catch (Exception e) { Notice.Send(e.ToString()); }
                    SystemLogSignal.WaitOne(500);
                }
            }
            catch (Exception e) { Notice.Send(e.ToString()); }
            stoppedSignal1.Set();
        }

        #endregion Logging

        protected virtual Sample GetSampleToRun()
        {
            var sampleFile = "runSample.json";
            var errorFile = "runSampleError.json";
            var activeFile = "runSampleActive.json";

            try
            {
                try { File.Delete(activeFile); } catch { }
                File.Move(sampleFile, activeFile, true);
                var jsonData = File.ReadAllText(activeFile);
                var runSample = JsonConvert.DeserializeObject<Sample>(jsonData);
                //runSample.Connect();
                try { File.Delete(errorFile); } catch { }
                return runSample;
            }
            catch
            {
                try { File.Move(activeFile, errorFile, true); } catch { }
                return null;
            }
        }

        protected virtual void RunAProvidedSample()
        {
            if (Busy || !AutoFeedEnabled) return;
            try
            {
                if (GetSampleToRun() is Sample runSample)
                {
                    var ip = runSample.InletPort;
                    if (ip == null) ip = InletPorts.FirstOrDefault();
                    if (ip != null)
                    {
                        ip.Sample = runSample;
                        ip.State = LinePort.States.Prepared;    // assume this was done by the sample provider
                        Task.Run(() => RunSampleAt(ip));
                    }
                }
            }
            catch { }   // ignore any errors
        }

        protected virtual void UpdateTimerCallback(object state)
        {
            try
            {
                if (!Stopping)
                    Update();
            }
            catch (Exception e) { Notice.Send(e.ToString()); }
        }

        int msUpdateLoop = 0;
        bool allDaqsOk = false;
        List<IDaq> daqs = CachedList<IDaq>();
        protected virtual void Update()
        {
            #region DAQs
            var daqsOk = true;
            foreach (var daq in daqs)
            {
                if (!daq.IsUp)
                {
                    daqsOk = false;
                    if (!daq.IsStreaming)
                        EventLog.LogParsimoniously(daq.Name + " is not streaming");
                    else if (!daq.DataAcquired)
                        EventLog.LogParsimoniously(daq.Name + ": waiting for stream to start");
                    else
                    {
                        var error = daq.Error;
                        if (error != default)
                            EventLog.LogParsimoniously(error);
                        daq.ClearError();
                    }
                }
            }
            if (!allDaqsOk && daqsOk)
            {
                var ugcFilter = ugCinMC?.Filter as ButterworthFilter;
                var pMCFilter = MC?.Manometer?.Filter as ButterworthFilter;
                if (ugcFilter != null && pMCFilter != null)
                    ugcFilter.SamplingFrequency = pMCFilter.SamplingFrequency;
            }
            allDaqsOk = daqsOk;
            #endregion DAQs

            #region Power failure watchdog
            if (Started && EnableWatchdogs)
                Power?.Update();
            #endregion Power failure watchdog

            #region 200 ms
            if (daqsOk && msUpdateLoop % 200 == 0)
            {
                SystemLogSignal.Set();
            }
            #endregion 200 ms

            #region 500 ms
            if (daqsOk && Started && msUpdateLoop % 500 == 0)
            {
                lowPrioritySignal.Set();
            }
            #endregion 500 ms

            #region 1000 ms
            if (daqsOk && Started && msUpdateLoop % 1000 == 0)
            {
                DeleteCompletedSamples();
                RunAProvidedSample();
            }
            #endregion 1000 ms

            if (msUpdateLoop % 3600000 == 0) msUpdateLoop = 0;
            msUpdateLoop += UpdateIntervalMilliseconds;
        }

        protected virtual void PostUpdateGR(IGraphiteReactor gr)
        {
            if (gr.Busy)
            {
                // GR.State is "Stop" for exactly one GR.Update() cycle.
                if (gr.State == GraphiteReactor.States.Stop)
                {
                    SampleLog.Record(
                        "Graphitization complete:\r\n" +
                        $"\tGraphite {gr.Contents}");
                    if (BusyGRCount() == 1 && !SampleIsRunning)  // the 1 is this GR; "Stop" is still 'Busy'
                    {
                        string msg = "Last graphite reactor finished.";
                        if (PreparedGRs() < 1)
                            msg += "\r\nGraphite reactors need service.";
                        Alert("Operator Needed", msg);
                    }
                }
            }
            else if (gr.State == GraphiteReactor.States.WaitService)
            {
                if (gr.Aliquot != null)
                {
                    IAliquot a = gr.Aliquot;
                    if (!a.ResidualMeasured)
                    {
                        double ambientTemperature = Manifold(gr).Temperature;
                        if (Math.Abs(ambientTemperature - gr.SampleTemperature) < 10 &&        // TODO: magic number
                            Math.Abs(ambientTemperature - gr.ColdfingerTemperature) < 10)
                        {
                            // residual is P/T (Torr/kelvin)
                            a.ResidualPressure = gr.Pressure / (ZeroDegreesC + ambientTemperature);

                            SampleLog.Record(
                                "Residual measurement:\r\n" +
                                $"\tGraphite {a.Name}\t{a.ResidualPressure:0.000}\tTorr/K"
                                );
                            a.ResidualMeasured = true;

                            if (a.ResidualPressure > MaximumResidual * a.ExpectedResidualPressure)
                            {
                                if (a.Tries > 1)
                                {
                                    SampleLog.Record(
                                        "Excessive residual pressure. Graphitization failed.\r\n" +
                                        $"\tGraphite {a.Name}"
                                        );
                                }
                                else
                                {
                                    SampleLog.Record(
                                        "Excessive residual pressure. Trying again.\r\n" +
                                        $"\tGraphite {a.Name}"
                                        );
                                    gr.Start();  // try again
                                    a.ResidualMeasured = false;
                                }
                            }
                        }
                    }
                }
            }
        }

        #region device event handlers

        protected virtual void OnMainsDown() =>
            Warn("System Warning", "Mains Power is down");

        protected virtual void OnMainsRestored() =>
            Warn("System Message", $"Mains Power restored (down {Power.MainsDownTimer.ElapsedMilliseconds} ms)");

        protected virtual void OnMainsFailed()
        {
            EventLog.Record("System Failure: Mains Power Failure");
            Alert("System Failure", "Mains Power Failure");
            Notice.Send("System Failure", "Mains Power Failure", Notice.Type.Tell);
            FindAll<VacuumSystem>().ForEach(vs =>
            {
                vs.Isolate();
                vs.IsolateManifold();
            });

            // TODO shut down
        }

        protected virtual void OnOverflowDetected() =>
            Warn("System Alert!", "LN Containment Failure");

        protected virtual void OnSlowToFill() =>
            Alert("System Warning!", "LN Manifold is slow to fill!");

        protected virtual void OnSlowToFreeze()
        {
            var coldfingers = FindAll<Coldfinger>();
            var on = coldfingers.FindAll(cf => cf.State == Coldfinger.States.Freezing);
            coldfingers.ForEach(cf => cf.Standby());
            string list = "";
            on.ForEach(cf => list += cf.Name + "? ");
            Warn("System Alert!", $"A coldfinger is slow to freeze. {list} System paused for operator.");
        }
        #endregion device event handlers

        protected virtual void LowPriorityActivities()
        {
            stoppedSignal2.Reset();
            try
            {
                while (!Stopping)
                {
                    if (EnableAutozero) ZeroPressureGauges();

                    GraphiteReactors?.ForEach(gr => { gr.Update(); PostUpdateGR(gr); });

                    SaveSettings();
                    lowPrioritySignal.WaitOne(500);
                }
            }
            catch (Exception e) { Notice.Send(e.ToString()); }
            stoppedSignal2.Set();
        }

        /// <summary>
        /// Event handler for MC temperature and pressure changes
        /// </summary>
        protected virtual void UpdateSampleMeasurement(object sender = null, PropertyChangedEventArgs e = null)
        {
            if (e?.PropertyName == nameof(IValue.Value))
                ugCinMC.Update(MicrogramsCarbon(MC));
        }


        // value > Km * sensitivity ==> meter needs zeroing
        protected virtual void ZeroIfNeeded(IMeter m, double Km)
        {
            if (m != null && Math.Abs(m.Value) >= Km * m.Sensitivity)
                m.ZeroNow();
        }

        protected virtual void ZeroPressureGauges() { }

        protected virtual void DeleteCompletedSamples()
        {
            Samples.Values.ToList().ForEach(s =>
            {
                if (s.AliquotsCount < 1 &&
                    s.d13CPort?.Sample != s &&
                    s.InletPort?.Sample is ISample ipSample &&
                    (ipSample != s || s.InletPort.State == LinePort.States.Complete))
                {
                    if (s.InletPort?.Sample == s)
                        s.InletPort.Sample = null;
                    s.Name = null;      // remove the sample from the NamedObject Dictionary.
                }
            });
        }

        #endregion Periodic system activities & maintenance

        #region Process Management

        #region Process Control Parameters
        /// <summary>
        /// Sets the named process control parameter to the given value.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        protected void SetParameter(string name, double value) =>
            SetParameter(new Parameter() { ParameterName = name, Value = value });

        /// <summary>
        /// Sets a process control parameter for the current Sample. Ignored if
        /// Sample is null.
        /// </summary>
        /// <param name="parameter"></param>
        public override void SetParameter(Parameter parameter) =>
            Sample?.SetParameter(parameter);

        /// <summary>
        /// Return the current value of the Sample's process control parameter with the given name,
        /// unless Sample is null, in which case return the value from CegsPreferences instead.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public double GetParameter(string name)
        {
            if (ProcessSequenceIsRunning)
                return Sample?.Parameter(name) ?? CegsPreferences.Parameter(name);
            else if (Sample != null && !double.IsNaN(Sample.Parameter(name)))
                return Sample.Parameter(name);
            else
                return CegsPreferences.Parameter(name);
        }

        /// <summary>
        /// Sets the parameter to double.NaN.
        /// </summary>
        /// <param name="name"></param>
        protected void ClearParameter(string name) => SetParameter(name, double.NaN);

        // * * * * * * * * * * * * * * * * * * * * * * * * * * * * * *
        // Parameters only need to be defined here if they are referenced within this class.
        // * * * * * * * * * * * * * * * * * * * * * * * * * * * * * *

        /// <summary>
        /// The effective gas load below which a volume is considered leak free.
        /// </summary>
        public double LeakTightTorrLitersPerSecond => GetParameter("LeakTightTorrLitersPerSecond");

        /// <summary>
        /// How long to evacuate freshly serviced graphite reactors.
        /// </summary>
        public double GRFirstEvacuationMinutes => GetParameter("GRFirstEvacuationMinutes");

        /// <summary>
        /// Minimum Graphitization time.
        /// </summary>
        public double MinimumGRMinutes => GetParameter("MinimumGRMinutes");
        public double PressureOverAtm => GetParameter("PressureOverAtm");
        public double OkPressure => GetParameter("OkPressure");
        public double CleanPressure => GetParameter("CleanPressure");
        public double VPInitialHePressure => GetParameter("VPInitialHePressure");
        public double VPErrorPressure => GetParameter("VPErrorPressure");
        public double MaximumResidual => GetParameter("MaximumResidual");
        public double IMO2Pressure => GetParameter("IMO2Pressure");
        public double FirstTrapBleedPressure => GetParameter("FirstTrapBleedPressure");
        public double FirstTrapEndPressure => GetParameter("FirstTrapEndPressure");
        public double FirstTrapFlowBypassPressure => GetParameter("FirstTrapFlowBypassPressure");
        public double IronPreconditionH2Pressure => GetParameter("IronPreconditionH2Pressure");
        public double IMPluggedTorrPerSecond => GetParameter("IMPluggedTorrPerSecond");
        public double IMLoadedTorrPerSecond => GetParameter("IMLoadedTorrPerSecond");
        public double GRCompleteTorrPerMinute => GetParameter("GRCompleteTorrPerMinute");
        public double RoomTemperature => GetParameter("RoomTemperature");
        public double SulfurTrapTemperature => GetParameter("SulfurTrapTemperature");
        public double IronPreconditioningTemperature => GetParameter("IronPreconditioningTemperature");
        public double IronPreconditioningTemperatureCushion => GetParameter("IronPreconditioningTemperatureCushion");
        public double CollectionMinutes => GetParameter("CollectionMinutes");
        public double ExtractionMinutes => GetParameter("ExtractionMinutes");
        public double CO2TransferMinutes => GetParameter("CO2TransferMinutes");
        public double MeasurementSeconds => GetParameter("MeasurementSeconds");
        public double IronPreconditioningMinutes => GetParameter("IronPreconditioningMinutes");
        public double QuartzFurnaceWarmupMinutes => GetParameter("QuartzFurnaceWarmupMinutes");
        public double SulfurTrapMinutes => GetParameter("SulfurTrapMinutes");
        public double H2_CO2GraphitizationRatio => GetParameter("H2_CO2GraphitizationRatio");
        public double H2DensityAdjustment => GetParameter("H2DensityAdjustment");
        public double SmallSampleMicrogramsCarbon => GetParameter("SmallSampleMicrogramsCarbon");
        public double MinimumUgCThatPermits_d13CSplit => GetParameter("MinimumUgCThatPermits_d13CSplit");
        public double DilutedSampleMicrogramsCarbon => GetParameter("DilutedSampleMicrogramsCarbon");
        public double MaximumSampleMicrogramsCarbon => GetParameter("MaximumSampleMicrogramsCarbon");

        /// <summary>
        /// Inlet Port sample furnace working setpoint ramp rate (degrees per minute).
        /// </summary>
        public double IpRampRate => GetParameter("IpRampRate");

        /// <summary>
        /// The Inlet Port sample furnace's target setpoint (the final setpoint when ramping).
        /// </summary>
        public double IpSetpoint => GetParameter("IpSetpoint");

        /// <summary>
        /// The desired Inlet Manifold pressure, used for filling or flow management.
        /// </summary>
        public double ImPressureTarget => GetParameter("ImPressureTarget");

        /// <summary>
        /// During sample collection, close the Inlet Port when the Inlet Manifold pressure falls to this value,
        /// provided that it is a number (i.e., not NaN).
        /// </summary>
        public double CollectCloseIpAtPressure => GetParameter("CollectCloseIpAtPressure");

        /// <summary>
        /// During sample collection, close the Inlet Port when the Coil Trap pressure falls to this value,
        /// provided that it is a number (i.e., not NaN).
        /// </summary>
        public double CollectCloseIpAtCtPressure => GetParameter("CollectCloseIpAtCtPressure");

        /// <summary>
        /// Stop collecting into the coil trap when the Inlet Port temperature rises to this value,
        /// provided that it is a number (i.e., not NaN).
        /// </summary>
        public double CollectUntilTemperatureRises => GetParameter("CollectUntilTemperatureRises");

        /// <summary>
        /// Stop collecting into the coil trap when the Inlet Port temperature falls to this value,
        /// provided that it is a number (i.e., not NaN).
        /// </summary>
        public double CollectUntilTemperatureFalls => GetParameter("CollectUntilTemperatureFalls");

        /// <summary>
        /// Stop collecting when the Coil Trap pressure falls to or below this value,
        /// provided that it is a number (i.e., not NaN).
        /// </summary>
        public double CollectUntilCtPressureFalls => GetParameter("CollectUntilCtPressureFalls");

        /// <summary>
        /// Stop collecting into the coil trap when this much time has elapsed.
        /// provided that the value is a number (i.e., not NaN).
        /// </summary>
        public double CollectUntilMinutes => GetParameter("CollectUntilMinutes");

        /// <summary>
        /// How many minutes to wait.
        /// </summary>
        public double WaitTimerMinutes => GetParameter("WaitTimerMinutes");

        /// <summary>
        /// What pressure to evacuate InletPort to.
        /// </summary>
        public double IpEvacuationPressure => GetParameter("IpEvacuationPressure");

        #endregion Process Control Parameters

        #region Process Control Properties

        /// <summary>
        /// Change the Inlet Port Sample furnace setpoint at a controlled
        /// ramp rate, rather than immediately to the given value.
        /// </summary>
        public virtual bool EnableIpSetpointRamp { get; set; } = false;

        /// <summary>
        /// Monitors the time elapsed since the current sample collection phase began.
        /// </summary>
        public Stopwatch CollectStopwatch { get; set; } = new Stopwatch();

        #endregion Process Control Properties

        #region Process Steps

        /// <summary>
        /// Wait for timer minutes.
        /// </summary>
        protected virtual void WaitForTimer()
        {
            ProcessStep.Start($"Wait for {WaitTimerMinutes:0} minutes");
            WaitFor(() => ProcessStep.Elapsed.TotalMinutes >= WaitTimerMinutes);
            ProcessStep.End();
        }

        /// <summary>
        /// Turn on the Inlet Port quartz furnace.
        /// </summary>
        protected virtual void TurnOnIpQuartzFurnace() => InletPort.QuartzFurnace.TurnOn();

        /// <summary>
        /// Turn off the Inlet Port quartz furnace.
        /// </summary>
        protected virtual void TurnOffIpQuartzFurnace() => InletPort.QuartzFurnace.TurnOff();

        /// <summary>
        /// Adjust the Inlet Port sample furnace setpoint.
        /// </summary>
        protected virtual void AdjustIpSetpoint()
        {
            if (IpSetpoint.IsNaN()) return;
            InletPort.SampleFurnace.Setpoint = IpSetpoint;
        }

        /// <summary>
        /// Wait for Inlet Port temperature to fall below IpSetpoint
        /// </summary>
        protected virtual void WaitIpFallToSetpoint()
        {
            AdjustIpSetpoint();
            bool shouldStop()
            {
                if (Stopping)
                    return true;
                if (InletPort.Temperature <= IpSetpoint)
                    return true;
                return false;
            }
            ProcessStep.Start($"Waiting for {InletPort.Name} to reach {IpSetpoint:0} °C");
            WaitFor(shouldStop, -1, 1000);
            ProcessStep.End();
        }

        /// <summary>
        /// Turn on the Inlet Port sample furnace.
        /// </summary>
        protected virtual void TurnOnIpSampleFurnace()
        {
            AdjustIpSetpoint();
            InletPort.SampleFurnace.TurnOn();
        }

        /// <summary>
        /// Wait for the InletPort sample furnace to reach the setpoint.
        /// </summary>
        protected virtual void WaitIpRiseToSetpoint()
        {
            bool shouldStop()
            {
                if (Stopping)
                    return true;
                if (InletPort.Temperature >= IpSetpoint)
                    return true;
                return false;
            }
            ProcessStep.Start($"Waiting for {InletPort.Name} to reach {IpSetpoint:0} °C");
            WaitFor(shouldStop, -1, 1000);
            ProcessStep.End();
        }

        /// <summary>
        /// Turn off the Inlet Port sample furnace.
        /// </summary>
        protected virtual void TurnOffIpSampleFurnace() => InletPort.SampleFurnace.TurnOff();


        /// <summary>
        /// Start collecting sample into the first trap.
        /// </summary>
        protected virtual void StartCollecting() => StartSampleFlow(true);

        protected virtual void StartSampleFlow(bool freezeTrap)
        {
            var collectionPath = IM_FirstTrap;
            var trap = FirstTrap;
            var status = freezeTrap ?
                $"Start collecting sample in {trap.Name}" :
                $"Start gas flow through {trap.Name}";
            ProcessStep.Start(status);

            if (freezeTrap)
                trap.WaitForFrozen(false);
            collectionPath.FlowValve?.CloseWait();
            InletPort.Open();
            Sample.CoilTrap = trap.Name;
            InletPort.State = LinePort.States.InProcess;
            CollectStopwatch.Restart();
            collectionPath.FlowManager?.Start(FirstTrapBleedPressure);

            ProcessStep.End();
        }

        /// <summary>
        /// Set all collection condition parameters to NaN
        /// </summary>
        protected virtual void ClearCollectionConditions()
        {
            ClearParameter("CollectUntilTemperatureRises");
            ClearParameter("CollectUntilTemperatureFalls");
            ClearParameter("CollectCloseIpAtPressure");
            ClearParameter("CollectCloseIpAtCtPressure");
            ClearParameter("CollectUntilCtPressureFalls");
            ClearParameter("CollectUntilMinutes");
        }

        string stoppedBecause = "";
        /// <summary>
        /// Wait for a collection stop condition to occur.
        /// </summary>
        protected virtual void CollectUntilConditionMet()
        {
            ProcessStep.Start($"Wait for a collection stop condition");

            bool shouldStop()
            {
                if (CollectStopwatch.IsRunning && CollectStopwatch.ElapsedMilliseconds < 1000)
                    return false;

                // TODO: what if flow manager becomes !Busy (because, e.g., FlowValve is fully open)?
                // TODO: should we invoke DuringBleed()? When?
                // TODO: should we disable/enable CT.VacuumSystem.Manometer?

                // Open flow bypass when conditions allow it without producing an excessive
                // downstream pressure spike.
                if (IM.Pressure - FirstTrap.Pressure < FirstTrapFlowBypassPressure)
                    FirstTrap.Open();   // open bypass if available


                if (CollectCloseIpAtPressure.IsANumber() && InletPort.IsOpened && IM.Pressure <= CollectCloseIpAtPressure)
                {
                    var p = IM.Pressure;
                    InletPort.Close();
                    SampleLog.Record($"{Sample.LabId}\tClosed {InletPort.Name} at {IM.Manometer.Name} = {p:0} Torr");
                }
                if (CollectCloseIpAtCtPressure.IsANumber() && InletPort.IsOpened && FirstTrap.Pressure <= CollectCloseIpAtCtPressure)
                {
                    var p = FirstTrap.Pressure;
                    InletPort.Close();
                    SampleLog.Record($"{Sample.LabId}\tClosed {InletPort.Name} at {FirstTrap.Manometer.Name} = {p:0} Torr");
                }

                if (Stopping)
                {
                    stoppedBecause = "CEGS is shutting down";
                    return true;
                }
                if (CollectUntilTemperatureRises.IsANumber() && InletPort.Temperature >= CollectUntilTemperatureRises)
                {
                    stoppedBecause = $"InletPort.Temperature rose to {CollectUntilTemperatureRises:0} °C";
                    return true;
                }
                if (CollectUntilTemperatureFalls.IsANumber() && InletPort.Temperature <= CollectUntilTemperatureFalls)
                {
                    stoppedBecause = $"InletPort.Temperature fell to {CollectUntilTemperatureFalls:0} °C";
                    return true;
                }

                if (CollectUntilCtPressureFalls.IsANumber() &&
                    FirstTrap.Pressure <= CollectUntilCtPressureFalls &&
                    IM.Pressure < Math.Ceiling(CollectUntilCtPressureFalls) + 2)
                {
                    stoppedBecause = $"{FirstTrap.Name}.Pressure fell to {CollectUntilCtPressureFalls:0.00} Torr";
                    return true;
                }

                // old?: FirstTrap.Pressure < FirstTrapEndPressure;
                if (FirstTrapEndPressure.IsANumber() &&
                    FirstTrap.Pressure <= FirstTrapEndPressure &&
                    IM.Pressure < Math.Ceiling(FirstTrapEndPressure) + 2)
                {
                    stoppedBecause = $"{FirstTrap.Name}.Pressure fell to {FirstTrapEndPressure:0.00} Torr";
                    return true;
                }

                if (CollectUntilMinutes.IsANumber() && CollectStopwatch.Elapsed.TotalMinutes >= CollectUntilMinutes)
                {
                    stoppedBecause = $"{MinutesString((int)CollectUntilMinutes)} elapsed";
                    return true;
                }

                stoppedBecause = "";
                return false;
            }
            WaitFor(shouldStop, -1, 1000);
            SampleLog.Record($"{Sample.LabId}\tStopped collecting:\t{stoppedBecause}");

            ProcessStep.End();
        }

        /// <summary>
        /// Stop collecting immediately
        /// </summary>
        protected virtual void StopCollecting() => StopCollecting(true);

        /// <summary>
        /// Close the IP and wait for CT pressure to bleed down until it stops falling.
        /// </summary>
        protected virtual void StopCollectingAfterBleedDown() => StopCollecting(false);

        /// <summary>
        /// Stop collecting. If 'immediately' is false, wait for CT pressure to bleed down after closing IP
        /// </summary>
        /// <param name="immediately">If false, wait for CT pressure to bleed down after closing IP</param>
        protected virtual void StopCollecting(bool immediately = true)
        {
            ProcessStep.Start("Stop Collecting");

            IM_FirstTrap.FlowManager?.Stop();
            InletPort.Close();
            InletPort.State = LinePort.States.Complete;
            if (!immediately)
                FinishCollecting();
            IM_FirstTrap.Close();
            FirstTrap.Isolate();
            FirstTrap.FlowValve?.CloseWait();

            ProcessStep.End();
        }

        /// <summary>
        /// Wait until the trap pressure stops falling
        /// </summary>
        protected virtual void FinishCollecting()
        {
            var p = FirstTrap?.Manometer;
            ProcessStep.Start($"Wait for {FirstTrap.Name} pressure to stop falling");
            WaitFor(() => !(p?.IsFalling ?? true)); // don't wait if there's no manometer
            ProcessStep.End();
        }

        /// <summary>
        /// Get the sample gases into the first trap.
        /// </summary>
        protected virtual void Collect()
        {
            var collectionPath = IM_FirstTrap;
            collectionPath.Isolate();
            collectionPath.FlowValve?.OpenWait();
            collectionPath.OpenAndEvacuate(OkPressure);
            if (collectionPath.FlowManager == null)
                collectionPath.IsolateFromVacuum();

            StartCollecting();
            CollectUntilConditionMet();
            StopCollecting(false);
            InletPort.State = LinePort.States.Complete;

            if (FirstTrap != VTT)
                TransferCO2FromCTToVTT();
        }

        #endregion Process Steps




        /// <summary>
        /// This method must be provided by the derived class.
        /// </summary>
        protected virtual void OverrideNeeded([CallerMemberName] string caller = default) =>
            Warn("Program Error", $"{Name} needs an override for {caller}().");

        protected virtual void SampleRecord(ISample sample) { }
        protected virtual void SampleRecord(IAliquot aliquot) { }


        /// <summary>
        /// The gas supply that delivers the specified gas to the destination.
        /// </summary>
        protected virtual GasSupply GasSupply(string gas, ISection destination)
        {
            if (FirstOrDefault<GasSupply>((s) => s.GasName == gas && s.Destination == destination) is GasSupply gasSupply)
                return gasSupply;

            //Warn("Process Alert!",
            //    $"Cannot admit {gas} into {destination.Name}. There is no such GasSupply.");
            return null;
        }

        /// <summary>
        /// The Section's He supply, if there is one; otherwise, its, N2 or Ar supply;
        /// null if none are found.
        /// </summary>
        protected virtual GasSupply InertGasSupply(ISection section) =>
            GasSupply("He", section) ?? GasSupply("N2", section) ?? GasSupply("Ar", section);

        /// <summary>
        /// Find the smallest Section that contains the port and has a manometer.
        /// Note: It usually is, but might not actually be, a manifold.
        /// </summary>
        /// <param name="port"></param>
        /// <returns>The section found, or null if there isn't one</returns>
        protected virtual Section Manifold(IPort port)
        {
            var sections = FindAll<Section>(s => (s.Ports?.Contains(port) ?? false) && s.Manometer != null);
            if (sections == null) return null;
            if (sections.Count == 0) return null;
            var smallest = sections.First();
            var smallestVolume = smallest.MilliLiters;
            foreach (var s in sections)
            {
                if (s.MilliLiters > 0 && (smallestVolume <= 0 || s.MilliLiters < smallestVolume))
                {
                    smallest = s;
                    smallestVolume = s.MilliLiters;
                }
            }
            return smallest;
        }


        /// <summary>
        /// Gets InletPort's manifold.
        /// </summary>
        /// <returns>false if InletPort is null or the manifold is not found.</returns>
        protected virtual bool IpIm(out ISection im)
        {
            im = Manifold(InletPort);
            if (InletPort == null)
            {
                Warn("Process Error", "No InletPort is selected.");
                return false;
            }
            if (im == null)
            {
                Warn("Process Error", $"Can't find manifold Section for {InletPort.Name}.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Gets InletPort's manifold and O2 gas supply.
        /// </summary>
        /// <returns>false if InletPort is null, or if the manifold or gas supply is not found.</returns>
        protected virtual bool IpImO2(out ISection im, out IGasSupply gs)
        {
            gs = null;
            if (IpIm(out im))
                gs = GasSupply("O2", im);
            return gs != null;
        }

        /// <summary>
        /// Gets InletPort's manifold and inert gas supply.
        /// </summary>
        /// <returns>false if InletPort is null, or if the manifold or gas supply is not found.</returns>
        protected virtual bool IpImInertGas(out ISection im, out IGasSupply gs)
        {
            gs = null;
            if (!IpIm(out im)) return false;
            gs = InertGasSupply(im);
            if (gs != null) return true;
            Warn("Configuration Error", $"Section {im.Name} has no inert GasSupply.");
            return false;
        }

        /// <summary>
        /// Gets the path from the InletPort to the FirstTrap
        /// </summary>
        /// <param name="im_trap"></param>
        /// <returns>Whether the path was found.</returns>
        protected virtual bool IpImToTrap(out ISection im_trap)
        {
            im_trap = null;
            if (!IpIm(out ISection im)) return false;

            var trap = FirstTrap;
            if (trap == null)
            {
                Warn("Configuration error", $"Can't find Section {trap.Name}");
                return false;
            }

            var firstChamber = im.Chambers?.First();
            var lastChamber = trap.Chambers?.Last();
            im_trap = FirstOrDefault<Section>(s =>
                s.Chambers?.First() == firstChamber &&
                s.Chambers?.Last() == lastChamber);

            if (im_trap == null)
            {
                Warn("Configuration error", $"Can't find Section linking {im.Name} and {trap.Name}");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Gets the GraphiteReactor's manifold.
        /// </summary>
        /// <returns>false if gr is null or the manifold is not found.</returns>
        protected virtual bool GrGm(IGraphiteReactor gr, out ISection gm)
        {
            gm = Manifold(gr);
            if (gr == null)
            {
                Warn("Process Error", "gr cannot be null.");
                return false;
            }
            if (gm == null)
            {
                Warn("Process Error", $"Can't find manifold Section for {gr.Name}.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Gets the GraphiteReactor's manifold and H2 gas supply.
        /// </summary>
        /// <returns>false if gr is null, or if the manifold or gas supply is not found.</returns>
        protected virtual bool GrGmH2(IGraphiteReactor gr, out ISection gm, out IGasSupply gs)
        {
            gs = null;
            if (GrGm(gr, out gm))
                gs = GasSupply("H2", gm);
            return gs != null;
        }

        /// <summary>
        /// Gets the GraphiteReactor's manifold and inert gas supply.
        /// </summary>
        /// <returns>false if gr is null, or if the manifold or gas supply is not found.</returns>
        protected virtual bool GrGmInertGas(IGraphiteReactor gr, out ISection gm, out IGasSupply gs)
        {
            gs = null;
            if (!GrGm(gr, out gm)) return false;
            gs = InertGasSupply(gm);
            if (gs != null) return true;
            Warn("Configuration Error", $"Section {gm.Name} has no inert GasSupply.");
            return false;
        }


        #region parameterized processes
        protected override void Combust(int temperature, int minutes, bool admitO2, bool openLine, bool waitForSetpoint)
        {
            if (!IpImO2(out ISection im, out IGasSupply O2)) return;

            if (admitO2)
            {
                ProcessStep.Start($"Combust at {temperature} °C, {MinutesString(minutes)}");
                AdmitIPO2();
            }
            else
                ProcessStep.Start($"Heat IP: {temperature} °C, {MinutesString(minutes)}");

            if (InletPort.SampleFurnace.IsOn)
                InletPort.SampleFurnace.Setpoint = temperature;
            else
                InletPort.SampleFurnace.TurnOn(temperature);

            if (openLine)
            {
                im.Evacuate(OkPressure);
                OpenLine();
            }

            if (waitForSetpoint)
            {
                ProcessStep.End();

                int closeEnough = temperature - 20;
                ProcessStep.Start($"Wait for {InletPort.SampleFurnace.Name} to reach {closeEnough} °C");
                while (InletPort.SampleFurnace.Temperature < closeEnough) Wait();
                ProcessStep.End();

                ProcessStep.Start($"Combust at {temperature} °C for {MinutesString(minutes)}.");
            }

            WaitRemaining(minutes);

            ProcessStep.End();
        }
        #endregion parameterized processes

        protected virtual void WaitForOperator()
        {
            Alert("Operator Needed", "Waiting for Operator.");
            Pause("Operator Needed", "Waiting for Operator.");
        }

        #region Valve operations

        protected virtual void ExerciseAllValves() => ExerciseAllValves(0);

        protected virtual void ExerciseAllValves(int secondsBetween)
        {
            ProcessStep.Start("Exercise all opened valves");
            foreach (var v in Valves?.Values)
                if ((v is CpwValve || v is PneumaticValve) && v.IsOpened)
                {
                    ProcessSubStep.Start($"Exercising {v.Name}");
                    v.Exercise();
                    if (secondsBetween > 0 )
                        WaitSeconds(secondsBetween);
                    ProcessSubStep.End();
                }
            ProcessStep.End();
        }

        protected virtual void CloseAllValves()
        {
            ProcessStep.Start("Exercise all opened valves");
            foreach (var v in Valves?.Values)
                if ((v is CpwValve || v is PneumaticValve) && !(v is RS232Valve) && v.IsOpened)
                    v.CloseWait();
            ProcessStep.End();
        }

        protected virtual void ExerciseLNValves()
        {
            ProcessStep.Start("Exercise all LN Manifold valves");
            foreach (var ftc in Coldfingers?.Values) ftc.LNValve.Exercise();
            ProcessStep.End();
        }

        protected virtual void CloseLNValves()
        {
            foreach (var ftc in Coldfingers?.Values) ftc.LNValve.Close();
        }

        protected virtual void CalibrateRS232Valves()
        {
            foreach (var v in Valves.Values)
                if (v is RS232Valve rv)
                    rv.Calibrate();
        }

        #endregion Valve operations

        #region Support and general purpose functions

        protected virtual void WaitForStablePressure(IVacuumSystem vacuumSystem, double pressure, int seconds = 5)
        {
            ProcessSubStep.Start($"Wait for stable pressure below {pressure} {vacuumSystem.Manometer.UnitSymbol}");
            vacuumSystem.WaitForStablePressure(pressure, seconds);
            ProcessSubStep.End();
        }

        protected virtual void TurnOffIPFurnaces() =>
            InletPort?.TurnOffFurnaces();

        protected virtual void HeatQuartz(bool openLine)
        {
            if (InletPort == null) return;

            ProcessStep.Start($"Heat Combustion Chamber Quartz ({QuartzFurnaceWarmupMinutes} minutes)");
            InletPort.QuartzFurnace.TurnOn();
            if (InletPort.State == LinePort.States.Loaded ||
                InletPort.State == LinePort.States.Prepared)
                InletPort.State = LinePort.States.InProcess;
            if (openLine) OpenLine();
            WaitRemaining((int)QuartzFurnaceWarmupMinutes);

            if (InletPort.NotifySampleFurnaceNeeded)
            {
                Alert("Operator Needed", $"{Sample?.LabId} is ready for sample furnace.");
                Notice.Send("Operator needed",
                    $"Remove any coolant from combustion tube and \r\n" +
                    $"raise the sample furnace at {InletPort.Name}.\r\n" +
                    "Press Ok to continue");
            }
            ProcessStep.End();
        }

        /// <summary>
        /// Heat the InletPort's quartz bed, while evacuating the rest
        /// of the line.
        /// </summary>
        [Description("Heat the InletPort's quartz bed, while evacuating the rest of the line.")]
        protected virtual void HeatQuartzOpenLine() => HeatQuartz(true);

        protected virtual void Admit(string gas, ISection destination, IPort port, double pressure)
        {
            if (!(GasSupply(gas, destination) is GasSupply gasSupply))
                return;
            gasSupply.Destination.ClosePorts();
            gasSupply.Admit(pressure);

            if (port != null)
            {
                ProcessSubStep.Start($"Admit {gasSupply.GasName} into {port.Name}");
                port.Open();
                Wait(2000);
                port.Close();
                ProcessSubStep.End();
                WaitSeconds(5);
            }
        }

        /// <summary>
        /// Admit O2 into the InletPort
        /// </summary>
        protected virtual void AdmitIPO2()
        {
            if (!IpIm(out ISection im)) return;
            Admit("O2", im, InletPort, IMO2Pressure);
        }

        protected virtual void AdmitIPInertGas(double pressure)
        {
            if (!IpImInertGas(out ISection im, out IGasSupply gs)) return;
            Admit(gs.GasName, im, InletPort, pressure);
        }

        protected virtual void DiscardIPGases()
        {
            if (!IpIm(out ISection im)) return;
            ProcessStep.Start($"Discard gases at ({InletPort.Name})");
            im.Isolate();
            InletPort.Open();
            WaitSeconds(10);                // give some time to record a measurement
            im.Evacuate(OkPressure);    // allow for high pressure due to water
            ProcessStep.End();
        }

        protected virtual void DiscardMCGases()
        {
            ProcessStep.Start("Discard sample from MC");
            SampleRecord(Sample);
            MC?.Evacuate();
            ProcessStep.End();
        }

        protected virtual void Flush(ISection section, int n, IPort port = null)
        {
            if (InertGasSupply(section) is GasSupply gs)
                gs.Flush(PressureOverAtm, 0.1, n, port);
            else
                Warn("Configuration Error", $"Section {section.Name} has no inert GasSupply.");
        }

        protected virtual void FlushIP()
        {
            if (!IpIm(out ISection im)) return;

            InletPort.State = LinePort.States.InProcess;
            EvacuateIP(0.1);
            Flush(im, 3, InletPort);

            // Residual inert gas is undesirable only to the extent that it
            // displaces O2. An O2 concentration of 99.99% -- more than
            // adequate for perfect combustion -- equates to 0.01% inert gas.
            // The admitted O2 pressure always exceeds 1000 Torr;
            // 0.01% of 1000 is 0.1 Torr.
            im.VacuumSystem.WaitForPressure(0.1);
            InletPort.Close();
        }

        protected virtual void CloseIP()
        {
            InletPort.Close();
        }

        #region GR operations
        protected virtual IGraphiteReactor NextGR(string thisOne, GraphiteReactor.Sizes size = GraphiteReactor.Sizes.Standard)
        {
            bool passedThisOne = false;
            IGraphiteReactor foundOne = null;
            foreach (var gr in GraphiteReactors)
            {
                if (passedThisOne)
                {
                    if (gr.Prepared && gr.Aliquot == null && gr.Size == size) return gr;
                }
                else
                {
                    if (foundOne == null && gr.Prepared && gr.Aliquot == null && gr.Size == size)
                        foundOne = gr;
                    if (gr.Name == thisOne)
                        passedThisOne = true;
                }
            }
            return foundOne;
        }

        protected virtual bool IsSulfurTrap(IGraphiteReactor gr) =>
            gr?.Aliquot?.Name == "sulfur";

        protected virtual IGraphiteReactor NextSulfurTrap(string thisGr)
        {
            bool passedThisOne = false;
            IGraphiteReactor foundOne = null;
            foreach (var gr in GraphiteReactors)
            {
                if (passedThisOne)
                {
                    if (IsSulfurTrap(gr) && gr.State != GraphiteReactor.States.WaitService) return gr;
                }
                else
                {
                    if (foundOne == null && IsSulfurTrap(gr) && gr.State != GraphiteReactor.States.WaitService)
                        foundOne = gr;
                    if (gr.Name == thisGr)
                        passedThisOne = true;
                }
            }
            if (foundOne != null) return foundOne;
            return NextGR(thisGr);
        }

        //protected virtual void OpenNextGRs()
        //{

        //    string grName = PriorGR;
        //    for (int i = 0; i < Sample.AliquotsCount; ++i)
        //    {
        //        if (NextGR(grName) is IGraphiteReactor gr)
        //        {
        //            gr.Open();
        //            grName = gr.Name;
        //        }
        //    }
        //}

        //protected virtual void OpenNextGRsAndd13C()
        //{
        //    if (!GrGm(NextGR(PriorGR), out ISection gm)) return;
        //    VacuumSystem.Isolate();
        //    OpenNextGRs();
        //    gm.JoinToVacuum();

        //    if (Sample.Take_d13C)
        //    {
        //        var port = d13CPort;
        //        if (port == null)
        //        {
        //            Warn("Sample Alert",
        //                $"Can't find d13C port for Sample {Sample.LabId} from {InletPort?.Name}");
        //        }
        //        else if (port.State == LinePort.States.Prepared)
        //        {
        //            var manifold = Manifold(port);
        //            if (manifold == null)
        //            {
        //                Warn("Configuration Error", $"Can't find manifold Section for d13C port {port.Name}.");
        //            }
        //            else
        //            {
        //                manifold.ClosePorts();
        //                manifold.Isolate();
        //                port.Open();
        //                manifold.JoinToVacuum();
        //            }
        //        }
        //    }
        //    Evacuate();
        //}

        protected virtual void CloseAllGRs() => CloseAllGRs(null);

        protected virtual void CloseAllGRs(IGraphiteReactor exceptGR)
        {
            foreach (var gr in GraphiteReactors)
                if (gr != exceptGR)
                    gr.Close();
        }

        protected virtual int BusyGRCount() => GraphiteReactors.Count(gr => gr.Busy);

        protected virtual int PreparedGRs() =>
            GraphiteReactors.Count(gr => gr.Prepared);

        protected virtual bool EnoughGRs()
        {
            int needed = Sample?.AliquotsCount ?? 1;
            if ((Sample?.SulfurSuspected ?? false) && !IsSulfurTrap(NextSulfurTrap(PriorGR)))
                needed++;
            return PreparedGRs() >= needed;
        }

        protected virtual void OpenPreparedGRs()
        {
            foreach (var gr in GraphiteReactors)
                if (gr.Prepared)
                    gr.Open();
        }

        protected virtual bool PreparedGRsAreOpened() =>
            !GraphiteReactors.Any(gr => gr.Prepared && !gr.IsOpened);

        protected virtual void ClosePreparedGRs()
        {
            foreach (var gr in GraphiteReactors)
                if (gr.Prepared)
                    gr.Close();
        }

        #region GR service

        protected virtual void PressurizeGRsWithInertGas(List<IGraphiteReactor> grs)
        {
            ProcessStep.Start("Backfill the graphite reactors with inert gas");
            var gasSupply = InertGasSupply(GM);
            if (gasSupply == null)
            {
                Warn("Configuration Error", $"Section {GM.Name} has no inert GasSupply.");
                return;
            }

            var pressure = Ambient.Pressure + 20;

            ProcessSubStep.Start($"Admit {pressure:0} Torr {gasSupply.GasName} into {GM.Name}");
            GM.ClosePorts();
            gasSupply.Admit();
            while (GM.Pressure < pressure)
                Wait();
            ProcessSubStep.End();


            ProcessSubStep.Start($"Open graphite reactors that are awaiting service.");
            grs.ForEach(gr => gr.Open());
            ProcessSubStep.End();

            ProcessSubStep.Start($"Ensure {GM.Name} pressure is ≥ {pressure:0} Torr");
            Wait(3000);
            while (GM.Pressure < pressure)
                Wait();
            gasSupply.ShutOff(true);
            ProcessSubStep.End();

            ProcessSubStep.Start("Isolate the graphite reactors");
            CloseAllGRs();
            ProcessSubStep.End();

            ProcessStep.End();
        }

        protected virtual void PrepareGRsForService()
        {
            var grs = new List<IGraphiteReactor>();
            foreach (var gr in GraphiteReactors)
            {
                if (gr.State == GraphiteReactor.States.WaitService)
                    grs.Add(gr);
                else if (gr.State == GraphiteReactor.States.Prepared && gr.Contents == "sulfur")
                    gr.ServiceComplete();
            }

            if (grs.Count < 1)
            {
                Notice.Send("Nothing to do", "No reactors are awaiting service.", Notice.Type.Tell);
                return;
            }

            grs.ForEach(gr => SampleRecord(gr.Aliquot));

            Notice.Send("Operator needed",
                "Mark Fe/C tubes with graphite IDs.\r\n" +
                "Press Ok to continue");

            PressurizeGRsWithInertGas(grs);

            PlaySound();
            Notice.Send("Operator needed", "Ready to load new iron and desiccant.");

            List<IAliquot> toDelete = new List<IAliquot>();
            grs.ForEach(gr =>
            {
                toDelete.Add(gr.Aliquot);
                gr.ServiceComplete();
            });

            toDelete.ForEach(a =>
            {
                if (a?.Sample is ISample s)
                {
                    s.Aliquots.Remove(a);
                    a.Name = null;          // remove the aliquot from the NamedObject Dictionary.
                }
            });
        }

        protected virtual bool AnyUnderTemp(List<IGraphiteReactor> grs, int targetTemp)
        {
            foreach (var gr in grs)
                if (gr.SampleTemperature < targetTemp)
                    return true;
            return false;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="section"></param>
        protected virtual void HoldForLeakTightness(ISection section)
        {
            // ports often have higher gas loads, usually due to water
            var leakRateLimit = 2 * LeakTightTorrLitersPerSecond;
            while (SectionLeakRate(section, leakRateLimit) > leakRateLimit)
                Pause("Process Exception", $"Something in the {section.Name} isn't vacuum-tight. Process Paused.");
        }

        protected virtual void PreconditionGRs()
        {
            var grs = GraphiteReactors.FindAll(gr => gr.State == GraphiteReactor.States.WaitPrep);
            if (grs.Count < 1)
            {
                Notice.Send("Nothing to do", "No reactors are awaiting preparation.", Notice.Type.Tell);
                return;
            }
            var gsInert = InertGasSupply(GM);
            if (gsInert == null)
            {
                Notice.Send("Configuration Error", "Can't find inert gas supply for GM.", Notice.Type.Tell);
                return;
            }
            var gsH2 = GasSupply("H2", GM);
            if (gsH2 == null)
            {
                Notice.Send("Configuration Error", "Can't find H2 gas supply for GM.", Notice.Type.Tell);
                return;
            }

            // close grs that aren't awaiting prep
            foreach (var gr in GraphiteReactors.Except(grs))
                gr.Close();

            var count = grs.Count;
            ProcessStep.Start($"Calibrate GR {"manometer".Plurality(count)} and {"volume".Plurality(count)}");

            // on the first flush, get the sizes
            ProcessSubStep.Start("Evacuate graphite reactors");
            GM.Isolate();
            grs.ForEach(gr => gr.Open());
            GM.OpenAndEvacuate();
            WaitForStablePressure(GM.VacuumSystem, CleanPressure);
            WaitMinutes((int)GRFirstEvacuationMinutes);
            HoldForLeakTightness(GM);
            ProcessSubStep.End();

            ProcessSubStep.Start($"Zero GR manometers.");
            grs.ForEach(gr => gr.Manometer.ZeroNow());
            while (grs.Any(gr => gr.Manometer.Zeroing))
                Wait();
            grs.ForEach(gr => gr.Close());
            ProcessSubStep.End();

            foreach (var gr in grs)
            {
                ProcessStep.Start($"Measure {gr.Name} volume");
                gsInert.Admit(PressureOverAtm);
                GM.Isolate();
                WaitSeconds(10);
                var p0 = GM.Manometer.WaitForAverage((int)MeasurementSeconds);
                var gmMilliLiters = GM.CurrentVolume(true);
                gr.Open();
                WaitSeconds(5);
                gr.Close();
                WaitSeconds(5);
                var p1 = GM.Manometer.WaitForAverage((int)MeasurementSeconds);

                ProcessSubStep.Start($"Calibrate {gr.Manometer.Name}");
                // TODO: make this safe and move it into AIVoltmeter
                var offset = gr.Manometer.Conversion.Operations[0];
                var v = offset.Execute((gr.Manometer as AIVoltmeter).Voltage);
                var gain = gr.Manometer.Conversion.Operations[1] as Arithmetic;
                gain.Operand = p1 / v;
                ProcessSubStep.End();

                gr.MilliLiters = gmMilliLiters * (p0 / p1 - 1);
                gr.Size = EnableSmallReactors && gr.MilliLiters < 2.0 ? GraphiteReactor.Sizes.Small : GraphiteReactor.Sizes.Standard;
                ProcessStep.End();
            }

            grs.ForEach(gr => gr.Open());
            GM.OpenAndEvacuate(OkPressure);
            ProcessStep.End();

            ProcessStep.Start("Evacuate & Flush GRs with inert gas");
            Flush(GM, 2);
            GM.VacuumSystem.WaitForPressure(OkPressure);
            ProcessStep.End();


            if (IronPreconditioningMinutes > 0)
            {
                ProcessStep.Start("Start Heating Fe");
                grs.ForEach(gr =>
                {
                    gr.Open();
                    gr.TurnOn(IronPreconditioningTemperature);
                });
                ProcessStep.End();

                int targetTemp = (int)IronPreconditioningTemperature - (int)IronPreconditioningTemperatureCushion;
                ProcessStep.Start("Wait for GRs to reach " + targetTemp.ToString() + " °C.");
                while (AnyUnderTemp(grs, targetTemp)) Wait();
                ProcessStep.End();

                if (IronPreconditionH2Pressure > 0)
                {
                    ProcessStep.Start("Admit H2 into GRs");
                    GM.IsolateFromVacuum();
                    gsH2.FlowPressurize(IronPreconditionH2Pressure);
                    grs.ForEach(gr => gr.Close());
                    ProcessStep.End();
                }

                ProcessStep.Start("Precondition iron for " + MinutesString((int)IronPreconditioningMinutes));
                if (IronPreconditionH2Pressure > 0)
                {
                    GM.OpenAndEvacuate(OkPressure);
                    OpenLine();
                }
                WaitRemaining((int)IronPreconditioningMinutes);
                ProcessStep.End();

                if (IronPreconditionH2Pressure > 0)
                {
                    ProcessStep.Start("Evacuate GRs");
                    GM.Isolate();
                    CloseAllGRs();
                    grs.ForEach(gr => { gr.Heater.TurnOff(); gr.Open(); });
                    GM.OpenAndEvacuate(OkPressure);
                    ProcessStep.End();

                    ProcessStep.Start("Flush GRs with inert gas");
                    Flush(GM, 3);
                    ProcessStep.End();
                }
                else
                {
                    grs.ForEach(gr => { gr.Heater.TurnOff(); gr.Open(); });
                }
            }

            grs.ForEach(gr => gr.PreparationComplete());

            OpenLine();
            Alert("Operator Needed", "Graphite reactor preparation complete");
        }

        protected virtual void PrepareIPsForCollection() =>
            PrepareIPsForCollection(null);

        protected virtual void PrepareIPsForCollection(List<IInletPort> ips = null)
        {
            if (ips == null)
                ips = InletPorts.FindAll(ip => ip.State == LinePort.States.Loaded);
            else
                ips = ips.FindAll(ip => ip.State == LinePort.States.Loaded);

            if (ips.Count < 1) return;

            // close ips that aren't awaiting prep
            foreach (var ip in InletPorts.Except(ips))
                ip.Close();

            ProcessStep.Start("Evacuate & Flush IPs with inert gas");

            IM.Isolate();
            ips.ForEach(ip => ip.Open());
            IM.Evacuate(OkPressure);
            WaitForStablePressure(IM.VacuumSystem, CleanPressure);
            HoldForLeakTightness(IM);

            Flush(IM, 3);
            IM.VacuumSystem.WaitForPressure(CleanPressure);

            ProcessStep.End();

            ips.ForEach(ip => ip.Close());

            ProcessStep.Start("Release the samples");
            var msg = "Release the samples at the following ports:";
            ips.ForEach(ip => msg += $"\r\n\t{ip?.Sample?.LabId} at {ip.Name}");

            Alert("Operator needed", "Release the prepared samples");
            Notice.Send("Operator needed", msg + "\r\n" +
                "Press Ok to continue", Notice.Type.Request);
            ProcessStep.End();

            ips.ForEach(ip => ip.State = LinePort.States.Prepared);

            //OpenLine();
        }

        protected virtual void Prepare_d13CPorts() { }


        protected virtual void ChangeSulfurFe()
        {
            var grs = GraphiteReactors.FindAll(gr =>
                IsSulfurTrap(gr) && gr.State == GraphiteReactor.States.WaitService);

            if (grs.Count < 1)
            {
                Notice.Send("Nothing to do", "No sulfur traps are awaiting service.", Notice.Type.Tell);
                return;
            }

            PressurizeGRsWithInertGas(grs);

            PlaySound();
            Notice.Send("Operator needed",
                "Replace iron in sulfur traps." + "\r\n" +
                "Press Ok to continue");

            // assume the Fe has been replaced

            ProcessStep.Start("Evacuate sulfur traps");
            GM.Isolate();
            grs.ForEach(gr => gr.Open());
            GM.OpenAndEvacuate(OkPressure);
            ProcessStep.End();

            ProcessStep.Start("Flush GRs with He");
            Flush(GM, 3);
            ProcessStep.End();

            grs.ForEach(gr => gr.PreparationComplete());

            OpenLine();
        }

        #endregion GR service

        #endregion GR operations

        #endregion Support and general purpose functions

        #region Vacuum System

        protected virtual void EvacuateIP(double pressure)
        {
            if (!IpIm(out ISection im)) return;
            im.OpenAndEvacuate(pressure, InletPort);
        }
        protected virtual void EvacuateIP() => EvacuateIP(OkPressure);

        #endregion Vacuum System

        #region Joining and isolating sections

        protected virtual void OpenLine() => OverrideNeeded();

        #endregion Joining and isolating sections

        #region Running samples

        public override void RunProcess(string processToRun)
        {
            EnsureProcessStartConditions();
            if (processToRun == "Run samples")
                RunSamples();
            else if (processToRun == "Run selected sample")
                RunSample();
            //else if (processToRun == "Run all samples")
            //    RunAllSamples();
            else
                base.RunProcess(processToRun);
        }

        /// <summary>
        /// These conditions are assumed at the start of a process.
        /// Sometimes they are changed during a process, which should always
        /// restore them to the "normal" state before or on completion. However,
        /// if the process is abnormally interrupted (e.g. aborted) they can
        /// be left in the incorrect state.
        /// </summary>
        protected virtual void EnsureProcessStartConditions()
        {
            //VacuumSystems.Values.ToList().ForEach(vs => vs.AutoManometer = true);
            //FindAll<GasSupply>().ForEach(gs => gs.ShutOff());
        }

        protected virtual void RunSample()
        {
            if (Sample == null)
            {
                if (InletPort != null)
                    Notice.Send("Process Error",
                       $"{InletPort.Name} does not contain a sample.",
                       Notice.Type.Tell);
                else
                    Notice.Send("Process Error",
                       $"No sample to run.",
                       Notice.Type.Tell);
                return;
            }

            if (InletPort != null && InletPort.State > LinePort.States.Prepared)
            {
                Notice.Send("Process Error",
                    $"{InletPort.Name} is not ready to run.",
                    Notice.Type.Tell);
                return;
            }

            if (LNManifolds.Values.All(x => x.SupplyEmpty))
            {
                double liters = LNManifolds.Values.Sum(x => x.Liters.Value);
                if (Notice.Send(
                        "System Alert!",
                        $"There might not be enough LN! ({liters:0.0} L)\r\n" +
                            "Press OK to proceed anyway, or Cancel to abort.",
                        Notice.Type.Warn).Text != "Ok")
                    return;
            }

            if (!EnoughGRs())
            {
                Notice.Send("Process Error",
                    "Unable to start process.\r\n" +
                    "Not enough GRs are prepared!",
                    Notice.Type.Tell);
                return;
            }

            if (!ProcessSequences.ContainsKey(Sample.Process))
            {
                Notice.Send("Process Error",
                    $"No such Process Sequence: '{Sample.Process}' ({Sample.Name} at {InletPort.Name} needs it.)",
                    Notice.Type.Tell);
                return;
            }

            CegsPreferences.DefaultParameters.ForEach(Sample.SetParameter);

            SampleLog.WriteLine("");
            Sample.DateTime = DateTime.Now;
            SampleLog.Record(
                $"Start Process:\t{Sample.Process}\r\n" +
                $"\t{Sample.LabId}\t{Sample.Milligrams:0.0000}\tmg\r\n" +
                $"\t{Sample.AliquotsCount}\t{"aliquot".Plurality(Sample.AliquotsCount)}");

            base.RunProcess(Sample.Process);
            while (base.Busy) Wait(1000);
        }

        protected virtual void RunSamples(bool all)
        {
            if (!(SelectSamples?.Invoke(all) is List<ISample> samples))
                samples = new List<ISample>();
            RunSamples(samples.Select(s => s.InletPort).ToList());
        }

        protected virtual void RunSamples() =>
            RunSamples(false);

        protected virtual void RunAllSamples() =>
            RunSamples(true);

        protected virtual void RunSamples(List<IInletPort> ips)
        {
            if (ips.Count < 1)
            {
                //Notice.Send("Process Error",
                //    "No InletPorts selected, or contain Samples that are ready to run.",
                //    Notice.Type.Tell);
                return;
            }

            ips.ForEach(RunSampleAt);
        }

        protected virtual void RunSampleAt(IInletPort ip)
        {
            InletPort = ip;
            RunSample();
        }

        protected override void ProcessStarting(string message = "")
        {
            if (message.IsBlank())
            {
                var sequence = ProcessType == ProcessTypeCode.Sequence ? "sequence " : "";
                var happened = "starting";
                message = $"Process {sequence}{happened}: {ProcessToRun}";
            }
            base.ProcessStarting(message);
        }

        protected override void ProcessEnded(string message = "")
        {
            if (message.IsBlank())
            {
                var sequence = ProcessType == ProcessTypeCode.Sequence ? "sequence " : "";
                var happened = RunCompleted ? "completed" : "aborted";
                message = $"Process {sequence}{happened}: {ProcessToRun}";
            }

            if (ProcessType == ProcessTypeCode.Sequence)
                SampleLog.Record(message + "\r\n\t" + Sample.LabId);

            Alert("System Status", message);

            base.ProcessEnded(message);
        }

        #endregion Running samples

        #region Sample loading and preparation

        /// <summary>
        /// Admit a quantity of dead CO2 into the MC.
        /// </summary>
        /// <param name="ugc_targetSize"></param>
        /// <param name="cleanItUp">Transfer it to the VTT and extract() before measuring.</param>
        protected virtual void AdmitDeadCO2(double ugc_targetSize, bool cleanItUp = false)
        {
            var CO2 = GasSupply("CO2", MC);
            if (CO2 == null) return;

            ProcessStep.Start("Evacuate MC");
            MC.Isolate();
            var aliquots = Sample?.AliquotsCount ?? 1;
            if (aliquots > 1)
                MC.Ports[0].Open();
            if (aliquots > 2)
                MC.Ports[1].Open();
            MC.Evacuate(CleanPressure);

            if (aliquots < 2)
                MC.Ports[0].Close();
            if (aliquots < 3)
                MC.Ports[1].Close();

            MC.VacuumSystem.WaitForPressure(CleanPressure);
            ZeroMC();
            ProcessStep.End();

            ProcessStep.Start("Admit CO2 into the MC");
            CO2.Pressurize(ugc_targetSize);
            ProcessStep.End();

            if (cleanItUp)
                CleanupCO2InMC();
            else
            {
                ProcessSubStep.Start("Take measurement");
                WaitForMCStable();
                SampleLog.Record($"Admitted CO2:\t{ugCinMC:0.0}\tµgC\t={ugCinMC / GramsCarbonPerMole:0.00}\tµmolC\t(target was {ugc_targetSize:0.0}\tµgC)");
                ProcessSubStep.End();
            }
        }

        protected virtual void AdmitDeadCO2()
            => AdmitDeadCO2(Sample.Micrograms, true);

        protected virtual void AdmitSealedCO2IP()
        {
            if (InletPort.State == LinePort.States.Prepared) return;    // already done
            if (!IpIm(out ISection im)) return;

            ProcessStep.Start($"Evacuate and flush {InletPort.Name}");
            im.ClosePortsExcept(InletPort);
            im.Isolate();
            InletPort.Open();
            im.Evacuate(OkPressure);
            WaitForStablePressure(im.VacuumSystem, OkPressure);
            Flush(im, 3);
            im.VacuumSystem.WaitForPressure(CleanPressure);
            ProcessStep.End();

            InletPort.Close();

            ProcessStep.Start("Release the sample");
            Alert("Operator Needed", $"Release sealed sample '{Sample.LabId}' at {InletPort.Name}.");
            Notice.Send("Operator needed",
                $"Release the sealed sample '{Sample.LabId}' at {InletPort.Name}.\r\n" +
                "Press Ok to continue");
            ProcessStep.End();
            InletPort.State = LinePort.States.Prepared;
        }

        /// <summary>
        /// prepare a carbonate sample for acidification
        /// </summary>
        protected virtual void PrepareCarbonateSample()
        {
            if (!IpImInertGas(out ISection im, out IGasSupply gs)) return;

            LoadCarbonateSample();
            EvacuateIP();
            Flush(im, 3, InletPort);

            ProcessStep.Start($"Wait for {im.VacuumSystem.Manometer.Name} < {CleanPressure:0.0e0} Torr");
            im.VacuumSystem.WaitForPressure(CleanPressure);

            gs.Admit();
            gs.WaitForPressure(PressureOverAtm);
            InletPort.Close();
            var pIM = im.Pressure;
            gs.ShutOff(true);

            ProcessStep.End();
            Alert("Operator Needed", $"Carbonate sample contains {pIM:0} Torr He");
            SampleLog.Record($"Carbonate vial pressure: {pIM:0}");

            OpenLine();
        }

        protected virtual void LoadCarbonateSample()
        {
            if (!IpImInertGas(out ISection im, out IGasSupply gs)) return;

            ProcessStep.Start($"Provide positive He pressure at {InletPort.Name} needle");
            IM.ClosePorts();
            IM.Isolate();
            gs.Admit();
            gs.WaitForPressure(PressureOverAtm);
            InletPort.Open();
            Wait(5000);
            gs.WaitForPressure(PressureOverAtm);
            ProcessStep.End();

            PlaySound();
            ProcessStep.Start("Remove previous sample or plug from IP needle");
            while (!IM.Manometer.IsFalling && ProcessStep.Elapsed.TotalSeconds < 10)
                Wait(); // wait up to 10 seconds for pIM clearly falling
            ProcessStep.End();

            ProcessStep.Start("Wait for stable He flow at IP needle");
            while (!IM.Manometer.IsStable)
                Wait();
            ProcessStep.End();

            PlaySound();
            ProcessStep.Start("Load next sample vial or plug at IP needle");
            while (IM.Manometer.RateOfChange < IMPluggedTorrPerSecond && ProcessStep.Elapsed.TotalSeconds < 20)
                Wait();
            if (IM.Manometer.RateOfChange > IMLoadedTorrPerSecond)
                InletPort.State = LinePort.States.Loaded;
            else
                InletPort.State = LinePort.States.Complete;
            ProcessStep.End();

            InletPort.Close();
            gs.ShutOff();
        }

        protected virtual void Evacuate_d13CPort()
        {
            if (!Sample.Take_d13C) return;
            var port = d13CPort;
            if (port == null)
            {
                Warn("Sample Alert",
                    $"Can't find d13C port for Sample {Sample.LabId} from {InletPort?.Name}");
                return;
            }
            if (port.State == LinePort.States.Prepared) return;
            if (port.State != LinePort.States.Loaded)
            {
                Alert("Sample Alert", $"d13C port {port.Name} is not available.");
                Notice.Send("Sample Alert",
                    $"d13C port {port.Name} is not available.\r\n" +
                    "It may contain a prior d13C sample.",
                    Notice.Type.Tell);
                return;
            }
            var manifold = Manifold(port);
            if (manifold == null)
            {
                Warn("Configuration Error", $"Can't find manifold Section for d13C port {port.Name}.");
                return;
            }
            ProcessStep.Start($"Prepare d13C port {port.Name}");
            manifold.ClosePorts();
            port.Open();
            manifold.OpenAndEvacuate(OkPressure);

            if (InertGasSupply(manifold) is GasSupply gs)
                Flush(manifold, 3, port);
            else
                Announce("Process Alert",
                    $"Unable to flush {port.Name}. There is no inert gas supply for {manifold.Name}");

            manifold.Evacuate(OkPressure);
            port.State = LinePort.States.Prepared;
            ProcessStep.End();
        }

        #endregion Sample loading and preparation

        #region Sample collection, extraction and measurement

        #region Collect

        protected virtual void FreezeVtt() => VTT.EmptyAndFreeze(CleanPressure);

        protected virtual void IpFreezeToTrap(ISection trap)
        {
            if (trap == null)
            {
                Warn("Configuration error", $"Can't find Section {trap.Name}");
                return;
            }

            if (!IpIm(out ISection im)) return;
            var im_trap = FirstOrDefault<Section>(s =>
                s.Chambers?.First() == im.Chambers?.First() &&
                s.Chambers?.Last() == trap.Chambers?.Last());

            if (im_trap == null)
            {
                Warn("Configuration error", $"Can't find Section linking {im.Name} and {trap.Name}");
                return;
            }

            ProcessStep.Start($"Evacuate {im.Name}");
            im.Evacuate(CleanPressure);
            WaitForStablePressure(im.VacuumSystem, CleanPressure);
            ProcessStep.End();

            ProcessStep.Start($"Evacuate and Freeze {trap.Name}");
            trap.EmptyAndFreeze(CleanPressure);
            trap.WaitForFrozen();
            ProcessStep.End();

            ProcessStep.Start($"Freeze the CO2 from {InletPort.Name} into the {trap.Name}");
            im_trap.Close();
            InletPort.State = LinePort.States.InProcess;
            InletPort.Open();
            im_trap.Open();
            WaitMinutes((int)CollectionMinutes);
            trap.Isolate();
            InletPort.Close();
            InletPort.State = LinePort.States.Complete;
            ProcessStep.End();
        }

        protected virtual void TransferCO2FromCTToVTT()
        {
            TransferCO2(FirstTrap, VTT);
        }

        #endregion Collect

        #region Extract
        protected virtual void ExtractAt(int targetTemp)
        {
            ProcessStep.Start($"Extract CO2 at {targetTemp:0} °C");
            //SampleLog.Record($"\tCO2 extraction temperature:\t{targetTemp:0}\t°C");

            VTT_MC.Isolate();
            VTT_MC.Close();

            var vtc = VTT.VTColdfinger;
            var ftcMC = MC.Coldfinger;
            vtc.Regulate(targetTemp);
            ftcMC.FreezeWait();

            targetTemp -= 1;            // continue at 1 deg under
            ProcessSubStep.Start($"Wait for {VTT.Name} to reach {targetTemp:0} °C");
            WaitFor(() => vtc.Temperature >= targetTemp);
            ProcessSubStep.End();

            VTT_MC.Open();
            WaitMinutes((int)ExtractionMinutes);
            ftcMC.RaiseLN();

            MC.Manometer.WaitForStable(5);
            MC.Isolate();
            vtc.Standby();

            ProcessStep.End();
        }


        protected virtual void Extract()
        {
            ProcessStep.Start($"Exctract CO2 from {VTT.Name} to {MC.Name}");
            MC.OpenAndEvacuateAll(CleanPressure);
            ZeroMC();
            MC.ClosePorts();
            MC.Isolate();
            ExtractAt(CO2ExtractionTemperature);
            ProcessStep.End();
        }

        #endregion Extract

        #region Measure

        protected virtual void WaitForMCStable(int seconds)
        {
            ProcessSubStep.Start($"Wait for μgC in MC to stabilize for {SecondsString(seconds)}");
            ugCinMC.WaitForStable(seconds);
            ProcessSubStep.End();
        }

        protected virtual void WaitForMCStable() => WaitForMCStable(5);

        protected virtual void ZeroMC()
        {
            WaitForMCStable();
            ProcessSubStep.Start($"Zero {MC.Manometer.Name}");
            MC.Manometer.ZeroNow();
            while (MC.Manometer.Zeroing) Wait();
            ProcessSubStep.End();
        }

        /// <summary>
        /// Apportion the currently SelectedMicrogramsCarbon into aliquots
        /// based on the MC chamber and port volumes.
        /// </summary>
        /// <param name="sample"></param>
        /// <returns></returns>
        protected virtual void ApportionAliquots()
        {
            var ugC = Sample.SelectedMicrogramsCarbon;
            // if no aliquots were specified, create one
            if (Sample.AliquotsCount < 1) Sample.AliquotsCount = 1;
            var n = Sample.AliquotsCount;
            var v0 = MC.Chambers[0].MilliLiters;
            var vTotal = v0;
            if (n > 1)
            {
                var v1 = MC.Ports[0].MilliLiters;
                vTotal += v1;
                if (n > 2)
                {
                    var v2 = MC.Ports[1].MilliLiters;
                    vTotal += v2;
                    Sample.Aliquots[2].MicrogramsCarbon = ugC * v2 / vTotal;
                }
                Sample.Aliquots[1].MicrogramsCarbon = ugC * v1 / vTotal;
            }
            Sample.Aliquots[0].MicrogramsCarbon = ugC * v0 / vTotal;
        }

        protected virtual void TakeMeasurement()
        {
            ProcessStep.Start("Take measurement");
            if (MC.Manometer.OverRange)
            {
                ProcessSubStep.Start("Expand sample into MC+MCU");
                MC.Ports[0].Open();
                WaitSeconds(15);
                ProcessSubStep.End();
                if (MC.Manometer.OverRange)
                {
                    ProcessSubStep.Start("Expand sample into MC+MCU+MCL");
                    MC.Ports[1].Open();
                    WaitSeconds(15);
                    ProcessSubStep.End();
                }
            }

            WaitForMCStable();

            // this is the measurement; a negative sample mass confounds H2 calculation, so it is disallowed
            double ugC = Math.Max(0, ugCinMC.WaitForAverage((int)MeasurementSeconds));
            if (Sample != null)
            {
                Sample.SelectedMicrogramsCarbon = ugC;
                ApportionAliquots();
            }

            string yield = "";
            if (Sample.TotalMicrogramsCarbon == 0)    // first measurement
            {
                yield = $"\tYield: {100 * ugC / Sample.Micrograms:0.00}%";
                Sample.TotalMicrogramsCarbon = ugC;
            }

            SampleLog.Record( "Sample measurement:\r\n" +
                $"\t{Sample.LabId}\t{Sample.Milligrams:0.0000}\tmg\r\n" +
                $"\tCarbon:\t{ugC:0.0} µgC (={ugC / GramsCarbonPerMole:0.00} µmolC){yield}"
            );
            ProcessStep.End();
        }


        protected virtual void Measure()
        {
            var ftcMC = MC.Coldfinger;

            ProcessStep.Start("Prepare to measure MC contents");
            MC.Isolate();

            if (ftcMC.IsActivelyCooling)
            {
                #region release incondensables
                ProcessStep.Start("Release incondensables");

                MC.OpenPorts();
                ftcMC.RaiseLN();
                MC.JoinToVacuum();
                MC.VacuumSystem.Evacuate(CleanPressure);

                ZeroMC();
                if ((Sample?.AliquotsCount ?? 1) < 3)
                {
                    MC.Ports[1].Close();
                    if ((Sample?.AliquotsCount ?? 1) < 2) MC.Ports[0].Close();
                    Wait(5000);
                }
                ProcessStep.End();
                #endregion release incondensables

                MC.Isolate();
            }

            if (!ftcMC.Thawed)
            {
                ProcessSubStep.Start("Bring MC to uniform temperature");
                ftcMC.Thaw();
                while (!ftcMC.Thawed)
                    Wait();
                ProcessSubStep.End();
            }

            ProcessStep.End();

            ProcessStep.Start("Measure Sample");
            TakeMeasurement();
            ProcessStep.End();
        }


        protected virtual void DiscardSplit()
        {
            ProcessStep.Start("Discard Excess sample");
            while (Sample.Aliquots[0].MicrogramsCarbon > MaximumSampleMicrogramsCarbon)
            {
                ProcessSubStep.Start("Evacuate Split");
                Split.Evacuate(CleanPressure);
                ProcessSubStep.End();

                ProcessSubStep.Start("Split sample");
                Split.IsolateFromVacuum();
                MC_Split.Open();

                var seconds = (int)Sample.Parameter("SplitEquilibrationSeconds");
                ProcessSubStep.Start($"Wait {SecondsString(seconds)} for sample to equilibrate.");
                WaitSeconds(seconds);
                ProcessSubStep.End();

                MC_Split.Close();
                ProcessSubStep.End();

                ProcessSubStep.Start("Discard split");
                Split.Evacuate(CleanPressure);
                ProcessSubStep.End();

                Sample.Discards++;
                SampleLog.Record(
                    $"Split discarded:\r\n\t{Sample.LabId}\t{Sample.Milligrams:0.0000}\tmg"
                );
                TakeMeasurement();
            }
            if (Sample.Discards > 0)
            {
                var splitRatio = 1 + Split.MilliLiters / MC.MilliLiters;
                var estimatedTotal = Sample.SelectedMicrogramsCarbon * Math.Pow(splitRatio, Sample.Discards);
                if (estimatedTotal > Sample.TotalMicrogramsCarbon)
                {
                    var ugC = Sample.TotalMicrogramsCarbon = estimatedTotal;
                    var yield = $"\tYield: {100 * ugC / Sample.Micrograms:0.00}%";

                    SampleLog.Record($"Updated TotalMicrogramsCarbon based on post-Discard measurement\r\n" +
                        $"\t{Sample.LabId}\t{Sample.Milligrams:0.0000}\tmg\r\n" +
                        $"\tCarbon:\t{ugC:0.0} µgC (={ugC / GramsCarbonPerMole:0.00} µmolC){yield}"
                    );
                }
            }
            ProcessStep.End();
        }

        #endregion Measure

        #region Graphitize

        protected virtual void DivideAliquots()
        {
            // When exactly two aliquots are to be taken, but MCP1 was opened
            // due to the total sample size, then before continuing, the sample
            // must be re-frozen from MC+MCP0+MCP1 to the MC, and then re-thawed,
            // expanding it into just the MC+MCP0 (i.e., after having closed MCP1).
            // In all other cases, the correct chambers should be opened already.
            if (Sample.AliquotsCount == 2 &&
                MC.Ports.Count > 2 &&
                MC.Ports[1].IsOpened) // MC.Ports[0].Opened can be presumed, since AliquotsCount == 2
            {
                var ftcMC = MC.Coldfinger;
                ftcMC.FreezeWait();

                ProcessSubStep.Start($"Wait for the sample to freeze in {MC.Name}");
                // CO2TransferMinutes is certainly excessive here, but how much is right?
                while (ProcessSubStep.Elapsed.TotalMinutes < CO2TransferMinutes/2)
                    Wait();
                ftcMC.RaiseLN();
                ProcessSubStep.End();

                MC.Ports[1].Close();

                ProcessSubStep.Start($"Thaw and expand sample into {MC.Name}..{MC.Ports[0].Name}");
                ftcMC.Thaw();
                while (!ftcMC.Thawed)
                    Wait();
                ProcessSubStep.End();
            }

            // Don't close the ports if only 1 aliquot; it might be
            // expanded to avoid an MC.Manometer.Overrange
            if (Sample.AliquotsCount > 1)
                MC.ClosePorts();
        }

        // TODO: move this routine to the graphite reactor class?
        protected virtual void TrapSulfur(IGraphiteReactor gr)
        {
            var ftc = gr.Coldfinger;
            var h = gr.Heater;

            ProcessStep.Start("Trap sulfur.");
            SampleLog.Record(
                $"Trap sulfur in {gr.Name} at {SulfurTrapTemperature} °C for {MinutesString((int)SulfurTrapMinutes)}");
            ftc.Thaw();
            gr.TurnOn(SulfurTrapTemperature);
            ProcessSubStep.Start($"Wait for {gr.Name} to reach sulfur trapping temperature (~{SulfurTrapTemperature} °C).");
            while (ftc.Temperature < 0 || gr.SampleTemperature < SulfurTrapTemperature - 5)
                Wait();
            ProcessSubStep.End();

            ProcessSubStep.Start("Hold for " + MinutesString((int)SulfurTrapMinutes));
            Wait((int)SulfurTrapMinutes * 60000);
            ProcessSubStep.End();

            h.TurnOff();
            ProcessStep.End();
        }

        protected virtual void RemoveSulfur()
        {
            if (!Sample.SulfurSuspected) return;

            ProcessStep.Start("Remove sulfur.");

            IGraphiteReactor gr = NextSulfurTrap(PriorGR);
            gr.Reserve("sulfur");
            gr.Aliquot.ResidualMeasured = true;    // prevent graphitization retry
            TransferCO2FromMCToGR(gr, 0, true);
            TrapSulfur(gr);
            TransferCO2FromGRToMC(gr, false);
            gr.State = GraphiteReactor.States.WaitService;

            ProcessStep.End();
            Measure();
        }

        protected virtual void Freeze(Aliquot aliquot)
        {
            if (aliquot == null) return;
            if (aliquot.Name.IsBlank())
            {
                aliquot.Name = NextGraphiteNumber.ToString();
                NextGraphiteNumber++;
            }

            var size = EnableSmallReactors && aliquot.MicrogramsCarbon <= SmallSampleMicrogramsCarbon ?
                GraphiteReactor.Sizes.Small :
                GraphiteReactor.Sizes.Standard;
            IGraphiteReactor gr = NextGR(PriorGR, size);
            if (gr == null)
            {
                Pause("Process exception!",
                    $"Can't find a suitable graphite reactor for this {aliquot.MicrogramsCarbon:0.0} µgC ({aliquot.MicromolesCarbon:0.00} µmol) aliquot.");
                return;
            }

            TransferCO2FromMCToGR(gr, aliquot.Sample.AliquotIndex(aliquot));
        }

        protected virtual double[] AdmitGasToPort(IGasSupply gs, double initialTargetPressure, IPort port)
        {
            if (!(gs?.FlowManager?.Meter is IMeter meter))
            {
                Warn("Process Error", $"AdmitGasToPort: {gs?.Name}.FlowManager.Meter is invalid.");
                return new double[] { double.NaN, double.NaN };
            }

            gs.Pressurize(initialTargetPressure);
            gs.IsolateFromVacuum();

            double pInitial = meter.WaitForAverage((int)MeasurementSeconds);
            if (port.Coldfinger?.IsActivelyCooling ?? false)
                port.Coldfinger.RaiseLN();

            ProcessSubStep.Start($"Admit {gs.GasName} into {port.Name}");
            port.Open();
            WaitSeconds(5);
            port.Close();
            ProcessSubStep.End();
            WaitSeconds(5);
            double pFinal = meter.WaitForAverage((int)MeasurementSeconds);
            return new double[] { pInitial, pFinal };
        }

        protected virtual void AddH2ToGR(IAliquot aliquot)
        {
            var gr = Find<IGraphiteReactor>(aliquot.GraphiteReactor);
            if (!GrGmH2(gr, out ISection gm, out IGasSupply H2)) return;

            double mL_GR = gr.MilliLiters;

            double nCO2 = aliquot.MicrogramsCarbon * CarbonAtomsPerMicrogram;  // number of CO2 particles in the aliquot
            double nH2target = H2_CO2GraphitizationRatio * nCO2;   // ideal number of H2 particles for the reaction

            // The pressure of nH2target in the frozen GR, where it will be denser.
            var targetFinalH2Pressure = Pressure(nH2target, mL_GR, gm.Temperature);
            // the small reactors don't seem to require the density adjustment.
            if (gr.Size == GraphiteReactor.Sizes.Standard)
                targetFinalH2Pressure *= H2DensityAdjustment;

            double nH2 = 0;
            double pH2ratio = 0;
            for (int i = 0; i < 3; ++i)     // up to three tries
            {
                var targetInitialH2Pressure = targetFinalH2Pressure +
                    Pressure(nH2target, gm.MilliLiters, gm.Temperature);

                // The GM pressure drifts a bit after the H2 is introduced, generally downward.
                // This value compensates for the consequent average error, which was about -4,
                // averaged over 14 samples in Feb-Mar 2018.
                // The compensation is bumped by a few more Torr to shift the variance in
                // target error toward the high side, as a slight excess of H2 is not
                // deleterious, whereas a deficiency could be.
                double driftAndVarianceCompensation = 9;    // TODO this should be a setting

                gm.Isolate();
                var p = AdmitGasToPort(
                    H2,
                    targetInitialH2Pressure + driftAndVarianceCompensation,
                    gr);
                var pH2initial = p[0];
                var pH2final = p[1];

                // this is what we actually got
                nH2 += Particles(pH2initial - pH2final, gm.MilliLiters, gm.Temperature);
                aliquot.H2CO2PressureRatio = pH2ratio = nH2 / nCO2;

                double nExpectedResidual;
                if (pH2ratio > H2_CO2StoichiometricRatio)
                    nExpectedResidual = nH2 - nCO2 * H2_CO2StoichiometricRatio;
                else
                    nExpectedResidual = nCO2 - nH2 / H2_CO2StoichiometricRatio;

                aliquot.InitialGmH2Pressure = pH2initial;
                aliquot.FinalGmH2Pressure = pH2final;
                aliquot.ExpectedResidualPressure = TorrPerKelvin(nExpectedResidual, mL_GR);

                SampleLog.Record(
                    $"GR hydrogen measurement:\r\n\t{Sample.LabId}\r\n\t" +
                    $"Graphite {aliquot.Name}\t{aliquot.MicrogramsCarbon:0.0}\tµgC\t={aliquot.MicromolesCarbon:0.00}\tµmolC\t{aliquot.GraphiteReactor}\t" +
                    $"pH2:CO2\t{pH2ratio:0.00}\t" +
                    $"{aliquot.InitialGmH2Pressure:0} => {aliquot.FinalGmH2Pressure:0}\r\n\t" +
                    $"expected residual:\t{aliquot.ExpectedResidualPressure:0.000}\tTorr/K"
                    );

                if (pH2ratio >= H2_CO2StoichiometricRatio * 1.05)
                    break;

                // try to add more H2
                targetFinalH2Pressure *= nH2target / nH2;

                if (targetFinalH2Pressure > 1500)       // way more than reasonable
                {
                    Warn("System Error",
                        $"Excessive H2 pressure required. The H2 is not going into {aliquot.GraphiteReactor}.\r\nProcess paused.");
                }
            }

            if (pH2ratio < H2_CO2StoichiometricRatio * 1.05)
            {
                Warn("Sample Alert",
                    $"Not enough H2 in {aliquot.GraphiteReactor}\r\nProcess paused.");
            }
        }

        protected virtual void Dilute()
        {
            if (Sample == null || DilutedSampleMicrogramsCarbon <= SmallSampleMicrogramsCarbon || Sample.TotalMicrogramsCarbon > SmallSampleMicrogramsCarbon) return;

            ProcessStep.Start($"Dilute sample to {DilutedSampleMicrogramsCarbon}");
            //Clean(VTT);
            TransferCO2FromMCToVTT();
            AdmitDeadCO2(DilutedSampleMicrogramsCarbon - Sample.TotalMicrogramsCarbon);
            TransferCO2FromMCToVTT();   // add the dilution gas to the sample
            Extract();
            Measure();
            ProcessStep.End();
        }

        protected virtual void FreezeAliquots()
        {
            foreach (Aliquot aliquot in Sample.Aliquots)
                Freeze(aliquot);
        }

        protected virtual void GraphitizeAliquots()
        {
            GM.IsolateFromVacuum();
            foreach (Aliquot aliquot in Sample.Aliquots)
            {
                ProcessStep.Start("Graphitize aliquot " + aliquot.Name);
                AddH2ToGR(aliquot);
                Find<IGraphiteReactor>(aliquot.GraphiteReactor).Start();
                ProcessStep.End();
            }
            GM.ClosePorts();
            GM.OpenAndEvacuate();
        }


        #endregion Graphitize

        protected virtual void CollectEtc()
        {
            Collect();
            ExtractEtc();
        }


        protected virtual void ExtractEtc()
        {
            Extract();
            MeasureEtc();
        }

        protected virtual void MeasureEtc()
        {
            Measure();
            DiscardSplit();
            RemoveSulfur();
            GraphitizeEtc();
        }

        protected virtual void GraphitizeEtc()
        {
            try
            {
                Dilute();
                DivideAliquots();
                FreezeAliquots();
                GraphitizeAliquots();
            }
            catch (Exception e) { Notice.Send(e.ToString()); }
        }

        #endregion Sample extraction and measurement

        #region Transfer CO2 between chambers

        // No foolproofing. All sections and coldfingers must be defined,
        // and the combined section must be named as expected.
        // If fromSection doesn't have a Coldfinger or VTColdfinger, this method
        // assumes fromSection is thawed (i.e., if there is an LN dewar on
        // fromSection, it must be removed before calling this method).
        protected virtual void TransferCO2(ISection fromSection, ISection toSection)
        {
            var combinedSection = Find<Section>(fromSection.Name + "_" + toSection.Name)
                ?? Find<Section>(toSection.Name + "_" + fromSection.Name);
            if (combinedSection == null)
                return;

            ProcessStep.Start($"Transfer CO2 from {fromSection.Name} to {toSection.Name}");

            fromSection.Isolate();

            var toSectionEvacuatesThroughFromSection =
                toSection.PathToVacuum.SafeIntersect(fromSection.Isolation).Count > 0;

            if (toSectionEvacuatesThroughFromSection)
            {
                toSection.Isolate();
                toSection.Freeze();
            }
            else
                toSection.EmptyAndFreeze(CleanPressure);

            // start thawing
            fromSection.Thaw();

            ProcessStep.Start("Wait for transfer start conditions.");
            WaitFor(() => toSection.Frozen && fromSection.Temperature > CO2TransferStartTemperature);
            ProcessStep.End();

            ProcessSubStep.Start($"Join {toSection.Name} to {fromSection.Name}");
            combinedSection.Isolate();
            combinedSection.Open();
            ProcessSubStep.End();

            ProcessSubStep.Start("Wait for CO2 transfer complete.");
            WaitMinutes((int)CO2TransferMinutes);
            ProcessSubStep.End();

            if (toSection.VTColdfinger == null && toSection.Coldfinger == null)
            {
                Pause("Operator Needed", $"Raise {toSection.Name} LN one inch.\r\n" +
                    "Press Ok to continue.");
                ProcessSubStep.Start("Wait for coldfinger to freeze.");
                WaitSeconds(30);
                ProcessSubStep.End();
            }
            else
                toSection.Coldfinger?.RaiseLN();    // no need to wait for VTC

            ProcessSubStep.Start($"Isolate {toSection.Name}");
            toSection.Isolate();
            ProcessSubStep.End();

            ProcessStep.End();
        }

        protected virtual void TransferCO2FromMCToVTT() =>
            TransferCO2(MC, VTT);

        protected virtual void TransferCO2FromMCToStandardGR() =>
            TransferCO2FromMCToGR(NextGR(PriorGR), 0);

        protected virtual void TransferCO2FromMCToGR() =>
            TransferCO2FromMCToGR(NextGR(PriorGR,
                EnableSmallReactors && ugCinMC <= SmallSampleMicrogramsCarbon ?
                    GraphiteReactor.Sizes.Small :
                    GraphiteReactor.Sizes.Standard), 0);

        protected virtual void Open_d13CPort(Id13CPort port) => port.Open();
        protected virtual void Close_d13CPort(Id13CPort port) => port.Close();

        protected virtual void TransferCO2FromMCToGR(IGraphiteReactor gr, int aliquotIndex = 0, bool skip_d13C = false)
        {
            if (gr == null) return;
            if (!GrGm(gr, out ISection gm)) return;

            var pathName = MC.Name + "_" + gm.Name;
            var mc_gm = Find<Section>(pathName);

            if (mc_gm == null)
            {
                Warn("Configuration error", $"Can't find Section {pathName}");
                return;
            }

            PriorGR = gr.Name;

            IAliquot aliquot = gr.Aliquot;
            if (aliquot == null && Sample != null && Sample.AliquotsCount > aliquotIndex)
            {
                aliquot = Sample.Aliquots[aliquotIndex];
                gr.Reserve(aliquot);
            }

            var take_d13C = !skip_d13C && (Sample?.Take_d13C ?? false) && aliquotIndex == 0;
            if (take_d13C && gr.Aliquot != null && gr.Aliquot.MicrogramsCarbon < MinimumUgCThatPermits_d13CSplit)
            {
                Warn("Process exception",
                    $"d13C was requested but the sample ({gr.Aliquot.MicromolesCarbon} µmol) is too small.");
                take_d13C = false;
            }

            Id13CPort d13CPort = take_d13C ? Guess_d13CPort(Sample) : null;

            ProcessStep.Start("Evacuate paths to sample destinations");
            MC.Isolate();
            gm.ClosePortsExcept(gr);
            if (gr.IsOpened)
                gm.IsolateExcept(gm.PathToVacuum);
            else
            {
                gm.Isolate();
                gr.Open();
            }

            var toBeOpened = gm.PathToVacuum.SafeUnion(gm.InternalValves);
            if (d13CPort != null)
            {
                if (d13CPort.ShouldBeClosed)
                {
                    Warn("Process Error",
                        $"Need to take d13C, but {d13CPort.Name} is not available.");
                }

                if (d13CPort.ShouldBeClosed)
                {
                    d13CPort = null;
                }
                else
                {
                    toBeOpened = toBeOpened.SafeUnion(d13C.PathToVacuum);
                    if (d13CM != null)
                    {
                        toBeOpened = toBeOpened.SafeUnion(d13CM.PathToVacuum);
                        if (d13CPort.IsOpened)
                            d13CM.IsolateExcept(toBeOpened);
                        else
                            d13CM.Isolate();
                        d13CM.ClosePortsExcept(d13CPort);
                    }
                    else
                    {
                        if (d13CPort.IsOpened)
                            d13C.IsolateExcept(toBeOpened);
                        else
                            d13C.Isolate();
                    }
                    Open_d13CPort(d13CPort);
                    Sample.d13CPort = d13CPort;
                }
            }
            gm.VacuumSystem.IsolateExcept(toBeOpened);
            toBeOpened.Open();
            gm.VacuumSystem.Evacuate(CleanPressure);
            gr.Manometer.ZeroNow(true); // wait to finish
            ProcessStep.End();

            ProcessStep.Start("Expand the sample");

            if (d13CPort != null)
            {
                if (d13CM == null)
                    Close_d13CPort(d13CPort);
                else
                    d13CM.Isolate();

                gr.Close();
            }

            mc_gm.IsolateFromVacuum();

            // release the sample
            var mcPort = aliquotIndex > 0 ? MC.Ports[aliquotIndex - 1] : null;
            mcPort?.Open();      // take it from from an MC port

            mc_gm.Open();

            ProcessStep.End();


            if (d13CPort != null)
            {
                ProcessStep.Start("Take d13C split");

                var seconds = (int)Sample.Parameter("SplitEquilibrationSeconds");
                ProcessSubStep.Start($"Wait {SecondsString(seconds)} for sample to equilibrate.");
                WaitSeconds(seconds);
                ProcessSubStep.End();

                d13C.IsolateFrom(mc_gm);
                Sample.Micrograms_d13C = aliquot.MicrogramsCarbon * d13C.MilliLiters / (d13C.MilliLiters + mc_gm.MilliLiters);
                aliquot.MicrogramsCarbon -= Sample.Micrograms_d13C;
                d13C.JoinTo(d13CM); // does nothing if there is no d13CM
                Open_d13CPort(d13CPort);
                d13CPort.State = LinePort.States.InProcess;
                d13CPort.Aliquot = aliquot;
                d13CPort.Coldfinger.Freeze();
                ProcessStep.End();

                ProcessStep.Start($"Freeze sample into {gr.Name} and {d13CPort.Name}");
                gr.Open();
            }
            else
                ProcessStep.Start($"Freeze sample into {gr.Name}");

            var grCF = gr.Coldfinger;
            grCF.Freeze();

            var grDone = false;
            var d13CDone = d13CPort == null;
            ProcessSubStep.Start($"Wait for coldfinger{(d13CPort == null ? "" : "s")} to freeze");
            while (!grDone || !d13CDone)
            {
                Wait();
                if (!grDone) grDone = grCF.Frozen;
                if (!d13CDone) d13CDone = d13CPort.Coldfinger.Frozen;
            }
            ProcessSubStep.End();

            WaitMinutes((int)CO2TransferMinutes);

            grDone = false;
            d13CDone = d13CPort == null;
            ProcessSubStep.Start("Raise LN");
            void jgr()
            {
                grCF.RaiseLN();
                gr.Close();
                // Note: removed release incondensables
            }
            void jd13()
            {
                if (d13CPort == null) return;
                d13CPort.Coldfinger.RaiseLN();
                Close_d13CPort(d13CPort);
                AddCarrierTo_d13C();
                d13CPort.Coldfinger.Standby();
                d13CPort.State = LinePort.States.Complete;
            }
            var tgr = Task.Run(jgr);
            var td13 = Task.Run(jd13);
            WaitFor(() => tgr.IsCompleted && td13.IsCompleted);

            ProcessStep.End();
        }

        protected virtual void AddCarrierTo_d13C() { }

        protected virtual void TransferCO2FromGRToMC() =>
            TransferCO2FromGRToMC(Find<IGraphiteReactor>(PriorGR), true);

        protected virtual void TransferCO2FromGRToMC(IGraphiteReactor gr, bool firstFreezeGR)
        {
            if (!GrGm(gr, out ISection gm)) return;
            var pathName = MC.Name + "_" + gm.Name;
            var mc_gm = Find<Section>(pathName);
            if (mc_gm == null)
            {
                Warn("Configuration error", $"Can't find Section {pathName}");
                return;
            }

            var grCF = gr.Coldfinger;

            ProcessStep.Start($"Transfer CO2 from {gr.Name} to {MC.Name}.");

            if (firstFreezeGR)
            {
                gr.Close();        // it should be closed already
                grCF.Freeze();
            }

            mc_gm.OpenAndEvacuate(CleanPressure);
            MC.ClosePorts();

            if (firstFreezeGR)
            {
                grCF.RaiseLN();

                ProcessSubStep.Start("Evacuate incondensables.");
                mc_gm.OpenAndEvacuate(CleanPressure);
                gr.Open();
                mc_gm.VacuumSystem.WaitForPressure(CleanPressure);
                mc_gm.IsolateFromVacuum();
                ProcessSubStep.End();
            }
            else
            {
                mc_gm.IsolateFromVacuum();
                ProcessSubStep.Start($"Open the path from {gr.Name} to {MC.Name}");
                gr.Open();
                mc_gm.Open();
                ProcessSubStep.End();
            }

            if (grCF.Temperature < grCF.NearAirTemperature) grCF.Thaw();
            var mcCF = MC.Coldfinger;
            mcCF.FreezeWait();

            ProcessSubStep.Start($"Wait for sample to freeze into {MC.Name}");
            while (ProcessSubStep.Elapsed.TotalMinutes < CO2TransferMinutes)
                Wait();
            mcCF.RaiseLN();

            mc_gm.Close();
            gr.Close();
            ProcessSubStep.End();

            ProcessStep.End();
        }


        /// This functionality is implementation-dependent.
        /// <summary>
        /// Transfer CO2 from the MC to the IP.
        /// </summary>
        protected virtual void TransferCO2FromMCToIP() {}


        /// <summary>
        /// Transfer CO2 from the MC to the IP via the VM.
        /// Useful when IM_MC contains a flow restriction.
        /// </summary>
        protected virtual void TransferCO2FromMCToIPViaVM()
        {
            if (!IpIm(out ISection im)) return;
            ProcessStep.Start($"Evacuate and join {InletPort.Name}..Split via VM");
            EvacuateIP();
            im.IsolateFromVacuum();
            Split.Evacuate();
            im.JoinToVacuum();
            im.VacuumSystem.WaitForPressure(0);
            ProcessStep.End();

            ProcessStep.Start($"Transfer CO2 from MC to {InletPort.Name}");
            Alert("Operator Needed", $"Put LN on {InletPort.Name}.");
            Notice.Send("Operator needed", $"Almost ready for LN on {InletPort.Name}.\r\n" +
                $"Press Ok to continue, then raise LN onto {InletPort.Name} coldfinger");

            im.VacuumSystem.Isolate();
            MC_Split.Open();

            ProcessSubStep.Start($"Wait {MinutesString((int)CO2TransferMinutes)} for CO2 to freeze into {InletPort.Name}");
            WaitMinutes((int)CO2TransferMinutes);
            ProcessSubStep.End();

            Alert("Operator Needed", $"Raise {InletPort.Name} LN.");
            Notice.Send("Operator needed", $"Raise {InletPort.Name} LN one inch.\r\n" +
                "Press Ok to continue.");

            WaitSeconds(30);
            InletPort.Close();
            ProcessStep.End();
        }

        #endregion Transfer CO2 between chambers

        #endregion Process Management

        #region Chamber volume calibration routines

        /// <summary>
        /// Install the CalibratedKnownVolume chamber in place of
        /// the port specified in the VolumeCalibration for the MC.
        /// Sets the value of MC.MilliLiters.
        /// </summary>
        protected virtual void MeasureVolumeMC()
        {
            FindAll<VolumeCalibration>().FirstOrDefault(vol => vol.ExpansionVolumeIsKnown)?.Calibrate();
        }

        /// <summary>
        /// Make sure the normal MC ports are installed (and not the
        /// CalibratedKnownVolume or a port plug).
        /// TODO: make sure the settings calibration order is preserved
        /// (add SequenceIndex property to VolumeCalibration?)
        /// </summary>
        protected virtual void MeasureRemainingVolumes()
        {
            VolumeCalibrations.Values.ToList().ForEach(vol =>
            { if (!vol.ExpansionVolumeIsKnown) vol.Calibrate(); });
        }

        /// <summary>
        /// Replace the chamber at the the port specified in the
        /// MC VolumeCalibration with a plug inserted flush to
        /// the fitting shoulder.
        /// Measures a valve's headspace and "OpenedVolumeDelta" which
        /// is the volume added to two chambers joined by the valve when
        /// the valve is opened, as compared to the combined volumes
        /// of the two chambers when the valve is closed.
        /// </summary>
        protected virtual void MeasureValveVolumes()
        {
            TestLog.Record($"MC\tMC+vH+vOD\tMC+vH");

            var volumeCalibrationPortValve = FindAll<VolumeCalibration>().FirstOrDefault(vol => vol.ExpansionVolumeIsKnown).Expansions?[0].ValveList?[0];
            double t2sum = 0, t3sum = 0;

            var n = 5;
            for (int i = 1; i <= n; ++i)
            {
                ProcessStep.Start($"Measure valve volumes (pass {i})");
                ProcessSubStep.Start($"Evacuate, admit gas");
                MC.Isolate();
                MC.OpenPorts();
                MC.Evacuate(OkPressure);
                MC.ClosePorts();
                var GasSupply = InertGasSupply(MC);
                GasSupply.Pressurize(95);
                ProcessSubStep.End();

                ProcessSubStep.Start($"get p0");
                WaitMinutes(1);
                var p0 = MC.Manometer.WaitForAverage((int)MeasurementSeconds) / (MC.Temperature + ZeroDegreesC);
                ProcessSubStep.End();

                // When compared to p2, p1 reveals the volume
                // difference between the valve's Opened and Closed
                // positions.
                ProcessSubStep.Start($"get p1");
                volumeCalibrationPortValve.Open();
                WaitMinutes(1);
                var p1 = MC.Manometer.WaitForAverage((int)MeasurementSeconds) / (MC.Temperature + ZeroDegreesC);
                ProcessSubStep.End();

                // When compared to p0, p2 reveals the valve's
                // port side headspace, if the port
                // is plugged flush with the fitting shoulder.
                ProcessSubStep.Start($"get p2");
                volumeCalibrationPortValve.Close();
                WaitMinutes(1);
                var p2 = MC.Manometer.WaitForAverage((int)MeasurementSeconds) / (MC.Temperature + ZeroDegreesC);
                TestLog.Record($"{p0:0.00000}\t{p1:0.00000}\t{p2:0.00000}");
                t2sum += p0 / p1 - 1;
                t3sum += p0 / p2 - 1;
                ProcessSubStep.End();
                ProcessStep.End();
            }
            var t2 = t2sum / n;
            var t3 = t3sum / n;

            // correct the initially determined MC chamber volume
            // to account for the KV port valve volumes
            var kv = Find<Chamber>("CalibratedKnownVolume").MilliLiters;
            var mc = Find<Chamber>("MC");
            var prior = mc.MilliLiters;
            var t1 = kv / prior;
            mc.MilliLiters = kv / (t1 - t3);
            TestLog.Record($"{mc.Name} (mL): {prior} => {mc.MilliLiters}");

            // valve headspace
            var vH = MC.MilliLiters * t3;
            TestLog.Record($"{volumeCalibrationPortValve.Name} headspace (mL) = {vH}");

            // valve OpenedVolumeDelta;
            var vOD = MC.MilliLiters * t2 - vH;
            TestLog.Record($"{volumeCalibrationPortValve.Name} OpenedVolumeDelta (mL) = {vOD}");

            OpenLine();
        }

        #endregion Chamber volume calibration routines

        #region Other calibrations


        protected virtual void CalibrateGRH2()
        {
            var gm = Manifold(GraphiteReactors[0]);

            //var gms = new List<Section>();
            //GraphiteReactors.ForEach(gr =>
            //{
            //    var gm = Manifold(gr);
            //    if (!gms.Contains(gm))
            //        gms.Add(gm);
            //});

            //gms.ForEach(gm =>
            //{
                var grs = new List<IGraphiteReactor>();
                GraphiteReactors.ForEach(gr =>
                {
                    if (Manifold(gr) == gm && gr.Prepared)
                        grs.Add(gr);
                });
                if (grs.Count > 0)
                    CalibrateGRH2(grs);
            //});
        }

        // all of the listed grs must be on the same manifold
        protected virtual void CalibrateGRH2(List<IGraphiteReactor> grs)
        {
            grs.ForEach(gr =>
            {
                if (!gr.Prepared)
                {
                    Pause("Error", "CalibrateGRH2() requires all of the listed grs to be Prepared");
                    return;
                }
            });

            ProcessStep.Start("Freeze graphite reactors");
            grs.ForEach(gr => gr.Coldfinger.Raise());
            while (grs.Any(gr => !gr.Coldfinger.Frozen))
                Wait();
            ProcessStep.End();

            TestLog.WriteLine();
            TestLog.Record("H2DensityRatio test");
            TestLog.Record("GR\tpInitial\tpFinal\tpNormalized\tpRatio");

            GrGmH2(grs[0], out ISection gm, out IGasSupply gs);

            ProcessStep.Start("Isolate graphite reactors");
            gm.ClosePorts();
            gm.Isolate();
            ProcessStep.End();
            for (int pH2 = 850; pH2 > 100; pH2 /= 2)
            {
                ProcessStep.Start($"Measure density ratios starting with {pH2:0} Torr");

                ProcessSubStep.Start("Evacuate graphite reactors");
                grs.ForEach(gr => gr.Open());
                gm.Evacuate(OkPressure);
                ProcessSubStep.End();

                ProcessSubStep.Start("Isolate graphite reactors");
                gm.ClosePorts();
                ProcessSubStep.End();


                ProcessSubStep.Start($"Pressurize {gm.Name} to {pH2:0} Torr");
                gs.Pressurize(pH2);
                gs.IsolateFromVacuum();
                ProcessSubStep.End();
                grs.ForEach(gr =>
                {
                    WaitSeconds(30);
                    var pInitial = gm.Manometer.WaitForAverage((int)MeasurementSeconds);

                    ProcessSubStep.Start($"Admit H2 into {gr.Name}");
                    gr.Coldfinger.RaiseLN();
                    gr.Open();
                    WaitSeconds(5);
                    gr.Close();
                    WaitSeconds(60);
                    ProcessSubStep.End();

                    var pFinal = gm.Manometer.WaitForAverage((int)MeasurementSeconds);

                    // pFinal is the pressure in the cold GR with n particles of H2,
                    // whereas p would be the pressure if the GR were at the GM temperature.
                    // densityAdjustment = pFinal / p
                    var n = Particles(pInitial - pFinal, gm.MilliLiters, gm.Temperature);
                    var p = Pressure(n, gr.MilliLiters, gm.Temperature);
                    // The above uses the measured, GR-specific volume. To avoid errors,
                    // this procedure should only be performed if the Fe and perchlorate
                    // tubes have never been altered since the GR volumes were measured.
                    TestLog.Record($"{gr.Name}\t{pInitial:0.00}\t{pFinal:0.00}\t{p:0.00}\t{pFinal / p:0.000}");
                });
                ProcessStep.End();
            }
            grs.ForEach(gr => gr.Coldfinger.Standby());
            OpenLine();
        }

        #endregion Other calibrations

        #region Test functions

        /// <summary>
        /// Measures the leak rate using a 2-minute rate of rise test.
        /// </summary>
        /// <param name="port"></param>
        /// <returns></returns>
        protected virtual double PortLeakRate(IPort port)
        {
            var manifold = Manifold(port);
            if (manifold == null) return 0;     // can't check; assume ok

            ProcessStep.Start($"Leak checking {port.Name}.");

            ProcessSubStep.Start($"Evacuate {manifold.Name}+{port.Name} to below {OkPressure} Torr");
            manifold.Isolate();
            manifold.ClosePortsExcept(port);
            manifold.Open();
            port.Open();
            // OkPressure is a convenient but high starting pressure; ideally, ror tests start at ultimate pressure.
            manifold.Evacuate(OkPressure);
            ProcessSubStep.End();

            ProcessStep.End();

            return SectionLeakRate(manifold, LeakTightTorrLitersPerSecond);
        }

        /// <summary>
        /// Checks a section's leak rate. The section must be currently isolated and under vacuum.
        /// </summary>
        /// <param name="section"></param>
        /// <returns>Torr L/sec</returns>
        protected double SectionLeakRate(ISection section, double leakRateLimit)
        {
            var testSeconds = 120;      // Aeon's standard rate-of-rise test duration

            ProcessStep.Start($"Leak checking {section.Name}.");

            // For completeness, PathToVacuum's equivalent set of chambers should be included, too. It's
            // neglected for now (it would add little because all Manifold(port)'s reach their VM except for MCP1 and MCP2).
            var liters = (section.CurrentVolume(true) + section.VacuumSystem.VacuumManifold.MilliLiters) / 1000;  // volume in Liters
            var torr = testSeconds * leakRateLimit / liters;    // change in pressure at leakRateLimit for testSeconds
            var torrLiters = torr * liters;

            var p0 = section.VacuumSystem.Pressure;
            section.VacuumSystem.Isolate();
            var torrLimit = p0 + torr;
            ProcessSubStep.Start($"Wait up to {testSeconds:0} seconds for {torrLimit:0.0e0} Torr");
            var leaky = WaitFor(() => section.VacuumSystem.Pressure > torrLimit, testSeconds * 1000, 1000);
            var elapsed = ProcessSubStep.Elapsed.TotalSeconds;
            torr = section.VacuumSystem.Pressure - p0;     // actual change in pressure
            ProcessSubStep.End();

            ProcessStep.End();
            return torr * liters / elapsed;
        }

        public void CalibrateManualHeater(IHeater h, IThermocouple tc)
        {
            var oneSecond = 1000;       // milliseconds
            var oneMinute = 60 * oneSecond;
            double pvTarget = 850;  // degrees C
            double pvLimit = 1000;  // degrees C

            if (!h.Config.ManualMode)
            {
                Announce("Invalid heater mode.", $"{h.Name} is not a manual-mode heater.");
                return;
            }

            if (tc == null)
            {
                Announce("Missing thermocouple.", "No calibration thermocouple provided.");
                return;
            }

            if (!Notice.Ok("Operator Needed", $"Move the calibration thermocouple to {h.Name}."))
                return;

            if (tc.Temperature > pvTarget - 50)
            {
                h.TurnOff();
                ProcessStep.Start($"Waiting for tCcal ({tc.Temperature:0} °C) to cool below {pvTarget - 50:0} °C");
                if (!WaitFor(() => tc.Temperature < pvTarget - 50, oneMinute, oneSecond))
                {
                    Announce("Error", "Calibration thermocouple is too hot.");
                    ProcessStep.End();
                    return;
                }
                ProcessStep.End();
            }

            ProcessStep.Start($"Calibrating {h.Name} power level");
            TestLog.Record($"Calibrating {h.Name}'s PowerLevel");

            var pvIncreasing = tc.Temperature * 0.90323 + 102.42;        // +25 @ 800 C, +100 @ 25 C
            ProcessSubStep.Start($"Pre-heat furnace: wait for {pvIncreasing:0} °C");
            h.PowerLevel = Math.Min(24, h.Config.MaximumPowerLevel);
            h.TurnOn();

            TestLog.Record($"{h.Name} calibration: Temperature = {tc.Temperature:0.00} °C; PowerLevel = {h.Config.PowerLevel}; Waiting for {pvIncreasing:0.00} °C.");
            if (!(WaitFor(() => tc.Temperature > pvIncreasing, 2 * oneMinute, oneSecond)))
            {
                h.TurnOff();
                Pause($"{h.Name} calibration aborted", $"Temperature isn't rising fast enough. Is the calibration thermocouple in {h.Name}?");
                return;
            }
            ProcessSubStep.End();
            ProcessSubStep.Start($"Wait for {pvTarget - 10:0} °C");
            WaitFor(() => tc.Temperature > pvTarget - 10, 5 * oneMinute, oneSecond);
            ProcessSubStep.End();

            double delta = 3;           // start at max
            while (!Stopping && Math.Abs(delta) > 0)
            {
                double tooMuchError = 10;
                double tooLong = 1;
                double error = 0;

                ProcessSubStep.Start($"PowerLevel = {h.Config.PowerLevel:0.00}; delta = {delta:0.00}; waiting to adjust.");
                bool changeNeeded()
                {
                    if (tc.Temperature > pvLimit) return true;
                    error = tc.Temperature - pvTarget;
                    ProcessSubStep.CurrentStep.Description =
                        $"{h.Config.PowerLevel:0.00}: {tc.Temperature:0.00} error={error:0.0}";
                    return ProcessSubStep.Elapsed.TotalSeconds > 30 && Math.Abs(error) > tooMuchError;
                }
                WaitFor(changeNeeded, (int)(tooLong * oneMinute), oneSecond);
                ProcessSubStep.End();

                if (tc.Temperature > pvLimit)
                {
                    RecordAlert($"{h.Name} calibration stopped", $"Temperature exceeded 1000 °C.");
                    break;
                }

                delta = -error * 0.01;
                if (delta >= 0 && delta < 0.01) delta = 0;
                if (delta < 0 && delta > -0.01) delta = -0.01;
                if (delta > 3) delta = 3;
                if (delta < -3) delta = -3;

                var co = h.Config.PowerLevel + delta;
                if (co > h.MaximumPowerLevel && ProcessStep.Elapsed.TotalMinutes > 10)
                {
                    RecordAlert($"{h.Name} calibration stopped", $"{h.Name}: co ({co:0.00}) > MaxPowerLevel {h.MaximumPowerLevel:0.00}.");
                    break;
                }

                h.PowerLevel = Math.Min(h.MaximumPowerLevel, h.Config.PowerLevel + delta);
                TestLog.Record($"{h.Name} calibration: {tc.Temperature:0.00} °C: setting PowerLevel to {h.Config.PowerLevel:0.00}.");
            }
            h.TurnOff();

            if (delta == 0)
            {
                RecordAlert($"{h.Name} calibration complete", $"{h.Name}'s PowerLevel is {h.Config.PowerLevel:0.00}.");
            }
            else
            {
                Alert($"{h.Name} calibration unsuccessful", $"Check the sample data log for details.");
            }
            ProcessStep.End();

            void RecordAlert(string caption, string message)
            {
                Alert(caption, message);
                TestLog.Record(caption + ". " + message);
            }
        }

        public void PidStepTest(params IHeater[] heaters)
        {
            double initialCO = 1.0;
            double stepCO = 4.0;
            int warmup = 60; // seconds
            Array.ForEach(heaters, h => { h.Manual(stepCO); h.TurnOn(); });

            WaitSeconds(warmup); // Preheat furnace.

            Array.ForEach(heaters, h => h.PowerLevel = initialCO);

            WaitMinutes(15); // Wait for temperature to stabalize.
            var names = new List<string>();
            Array.ForEach(heaters, h => names.Add(h.Name));
            var log = new DataLog($"PID Step test{(heaters.Length > 1 ? "s" : "")} {string.Join(',', names)}.txt")
            {
                //Header = string.Join('\t', heaters as IEnumerable),
                ChangeTimeoutMilliseconds = 3 * 1000,
                OnlyLogWhenChanged = false,
                InsertLatestSkippedEntry = false
            };
            var columns = new ObservableList<DataLog.Column>();
            Array.ForEach(heaters, h =>
            {
                columns.Add(new DataLog.Column()
                {
                    Name = h.Name,
                    Resolution = -1.0,
                    Format = "0.00"
                });
            });
            log.Columns = columns;
            HacsLog.List.Add(log);

            Array.ForEach(heaters, h => h.PowerLevel = stepCO);

            WaitMinutes(3);

            log.ChangeTimeoutMilliseconds = 30 * 1000;

            WaitMinutes(17);

            HacsLog.List.Remove(log);
            log.Close();
            log = null;

            Array.ForEach(heaters, h => h.TurnOff());
        }

        /// <summary>
        /// Transfer CO2 from MC to IP,
        /// optionally add some carrier gas,
        /// then Collect(), Extract() and Measure()
        /// </summary>
        protected virtual void CO2LoopMC_IP_MC()
        {
            TransferCO2FromMCToIP();
            admitCarrier?.Invoke();

            Alert("Operator Needed", $"Thaw {InletPort.Name}.");
            Notice.Send("Operator needed",
                $"Remove LN from {InletPort.Name} and thaw the coldfinger.\r\n" +
                "Press Ok to continue");

            Collect();
            Extract();
            Measure();
        }


        /// <summary>
        /// Transfers CO2 from MC to VTT, then performs extract() and measure()
        /// </summary>
        protected virtual void CleanupCO2InMC()
        {
            TransferCO2FromMCToVTT();
            Extract();
            Measure();
        }

        /// <summary>
        /// Admit maximum carrier gas to the IM+VM and join to the IP.
        /// </summary>
        protected virtual void AdmitIPMax()
        {
            IpIm(out ISection im);
            im.Evacuate(OkPressure);
            im.ClosePorts();

            var gs = GasSupply("O2", im) ?? InertGasSupply(im);

            //for (int i = 0; i < amountOfCarrier; ++i)
            //{
                gs.Admit();       // dunno, 1000-1500 Torr?
                Wait(1000);
                gs.ShutOff();
                im.VacuumSystem.Isolate();
                im.JoinToVacuum();      // one cycle might keep ~10% in the IM
                WaitSeconds(3);
            //im.Isolate();
            //}

            InletPort.Open();
            WaitSeconds(3);
            InletPort.Close();
            im.Evacuate();
        }

        /// <summary>
        /// Admit some carrier gas into the IM and join to the IP.
        /// </summary>
        protected virtual void AdmitIPPuffs()
        {
            IpIm(out ISection im);
            im.Evacuate(OkPressure);
            im.ClosePorts();

            var gs = Find<GasSupply>("O2." + im.Name);
            if (gs == null) gs = InertGasSupply(im);

            for (int i = 0; i < amountOfCarrier; ++i)
            {
                gs.Admit();       // dunno, 1000-1500 Torr?
                Wait(1000);
                gs.ShutOff();
                im.VacuumSystem.Isolate();
                im.JoinToVacuum();      // one cycle might keep ~10% in the IM
                Wait(2000);
                im.Isolate();
            }

            InletPort.Open();
        }


        protected virtual void AdmitIPO2EvacuateIM()
        {
            AdmitIPO2();
            IM.Evacuate();
        }

        protected virtual void MeasureExtractEfficiency()
        {
            TestLog.WriteLine("\r\n");
            TestLog.Record("Measure VTT extract efficiency");
            MeasureProcessEfficiency(CleanupCO2InMC);
        }


        protected Action admitCarrier;
        protected int amountOfCarrier;
        protected virtual void MeasureIpCollectionEfficiency()
        {
            TestLog.WriteLine("\r\n");
            TestLog.Record("IP collection efficiency test");
            if (FirstTrap.FlowManager == null)
                admitCarrier = null;
            else
            {
                admitCarrier = AdmitIPMax;
                //amountOfCarrier = 3;    // puffs
            }
            MeasureProcessEfficiency(CO2LoopMC_IP_MC);
        }



        /// <summary>
        /// Simulates an organic extraction
        /// </summary>
        protected virtual void MeasureOrganicExtractEfficiency()
        {
            TestLog.WriteLine("\r\n");
            TestLog.Record("Organic bleed yield test");
            TestLog.Record($"Bleed target: {FirstTrapBleedPressure} Torr");
            admitCarrier = AdmitIPO2EvacuateIM;
            MeasureProcessEfficiency(CO2LoopMC_IP_MC);
        }




        /// <summary>
        /// Set the Sample LabId to the desired number of loops
        /// Set the Sample mass to the desired starting quantity
        /// If there is at least 80% of the desired starting quantity
        /// already in the measurement chamber, that will be used
        /// instead of admitting fresh gas.
        /// </summary>
        /// <param name="transferLoop">method to move sample from MC to somewhere else and back</param>
        protected virtual void MeasureProcessEfficiency(Action transferLoop)
        {
            if (Sample == null)
            {
                Notice.Send("A sample must be defined in order to test process efficiency.", Notice.Type.Tell);
                return;
            }

            ProcessStep.Start("Measure transfer efficiency");
            if (ugCinMC < Sample.Micrograms * 0.8)
            {
                OpenLine();
                MC.VacuumSystem.WaitForPressure(CleanPressure);
                AdmitDeadCO2();
            }

            int n; try { n = int.Parse(Sample.LabId); } catch { n = 1; }
            for (int repeats = 0; repeats < n; repeats++)
            {
                Sample.Micrograms = Sample.TotalMicrogramsCarbon;
                transferLoop?.Invoke();
            }
            ProcessStep.End();
        }


        // Discards the MC contents soon after they reach the
        // temperature at which they were extracted.
        protected virtual void DiscardExtractedGases()
        {
            ProcessStep.Start("Discard extracted gases");
            var mcCF = MC.Coldfinger;
            mcCF.Thaw();
            ProcessSubStep.Start("Wait for MC coldfinger to thaw enough.");
            while (mcCF.Temperature <= VTT.VTColdfinger.Setpoint + 10) Wait();
            ProcessSubStep.End();
            mcCF.Standby();    // stop thawing to save time

            // record pressure
            SampleLog.Record($"\tPressure of pre-CO2 discarded gases:\t{MC.Pressure:0.000}\tTorr");

            VTT_MC.OpenAndEvacuate(OkPressure);
            VTT_MC.IsolateFromVacuum();
            ProcessStep.End();
        }

        protected virtual void StepExtract()
        {
            // The equilibrium temperature of HCl at pressures from ~(1e-5..1e1)
            // is about 14 degC or more colder than CO2 at the same pressure.
            ExtractAt(-160);        // targets HCl
            DiscardExtractedGases();
            ExtractAt(-140);        // targets CO2
        }

        protected virtual void StepExtractionYieldTest()
        {
            Sample.LabId = "Step Extraction Yield Test";
            //admitDeadCO2(1000);
            Measure();

            TransferCO2FromMCToVTT();
            Extract();
            Measure();

            //transfer_CO2_MC_VTT();
            //step_extract();
            //VTT.Stop();
            //measure();
        }

        void ValvePositionDriftTest()
        {
            var v = FirstOrDefault<RS232Valve>();
            var pos = v.ClosedValue / 2;
            var op = new ActuatorOperation()
            {
                Name = "test",
                Value = pos,
                Incremental = false
            };
            v.ActuatorOperations.Add(op);

            v.DoWait(op);

            //op.Incremental = true;
            var rand = new Random();
            for (int i = 0; i < 100; i++)
            {
                op.Value = pos + rand.Next(-15, 16);
                v.DoWait(op);
            }
            op.Value = pos;
            op.Incremental = false;
            v.DoWait(op);

            v.ActuatorOperations.Remove(op);
        }

        void TestPort(IPort p)
        {
            for (int i = 0; i < 5; ++i)
            {
                p.Open();
                p.Close();
            }
            p.Open();
            WaitMinutes(5);
            p.Close();
        }

        // two minutes of moving the valve at a moderate pace
        void TestValve(IValve v)
        {
            TestLog.Record($"Operating {v.Name} for 2 minutes");
            for (int i = 0; i < 24; ++i)
            {
                v.CloseWait();
                WaitSeconds(2);
                v.OpenWait();
                WaitSeconds(2);
            }
        }

        void TestUpstream(IValve v)
        {
            TestLog.Record($"Checking {v.Name}'s 10-minute bump");
            v.OpenWait();
            WaitMinutes(5);     // empty the upstream side (assumes the downstream side is under vacuum)
            v.CloseWait();
            WaitMinutes(10);    // let the upstream pressure rise for 10 minutes
            v.OpenWait();       // how big is the pressure bump?
        }


        protected virtual void ExercisePorts(ISection s)
        {
            s.Isolate();
            s.Open();
            s.OpenPorts();
            WaitSeconds(5);
            s.ClosePorts();
            s.Evacuate(OkPressure);
        }

        protected virtual void TestAdmit(string gasSupply, double pressure)
        {
            var gs = Find<GasSupply>(gasSupply);
            gs?.Destination?.OpenAndEvacuate();
            gs?.Destination?.ClosePorts();
            gs?.Admit(pressure);
            WaitSeconds(10);
            EventLog.Record($"Admit test: {gasSupply}, target: {pressure:0.###}, stabilized: {gs.Meter.Value:0.###} in {ProcessStep.Elapsed:m':'ss}");
            gs?.Destination?.OpenAndEvacuate();
        }

        protected virtual void TestPressurize(string gasSupply, double pressure)
        {
            var gs = Find<GasSupply>(gasSupply);
            gs?.Destination?.OpenAndEvacuate(OkPressure);
            gs?.Destination?.ClosePorts();
            gs?.Pressurize(pressure);
            WaitSeconds(60);
            EventLog.Record($"Pressurize test: {gasSupply}, target: {pressure:0.###}, stabilized: {gs.Meter.Value:0.###} in {ProcessStep.Elapsed:m':'ss}");
            gs?.Destination?.OpenAndEvacuate();
        }



        protected virtual void Test() { }

        #endregion Test functions
    }
}
