import { useEffect, useState } from 'react';
import * as signalR from '@microsoft/signalr';
import { LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer } from 'recharts';

const API_BASE = 'http://localhost:5080';

interface TelemetryData {
  id: number;
  sessionId: string;
  activePlayers: number;
  serverStatus: string;
  activeModifiers: string;
  createdAt: string;
}

interface AudioFrameMetrics {
  channelId: string;
  samplesProcessed: number;
  rmsDb: number;
  peakDb: number;
  isClipping: boolean;
  isDeadAir: boolean;
  totalFramesProcessed: number;
  underrunCount: number;
  overrunCount: number;
  bufferFillRatio: number;
  timestampUtc: string;
}

interface StreamIncident {
  id: number;
  channelKey: string;
  channelName: string;
  incidentType: string;
  severity: string;
  rmsDb: number;
  details: string;
  createdAtUtc: string;
}

type SimMode = 'NormalVoice' | 'PttIdle' | 'DeadAir' | 'ClippingSpike' | 'JitterUnderrun';

const MODE_META: Record<SimMode, { label: string; summary: string }> = {
  NormalVoice: {
    label: 'Voice',
    summary: 'PTT keyed at normal speaking level (-14 to -6 dBFS).',
  },
  PttIdle: {
    label: 'PTT idle',
    summary: 'PTT released between transmissions. Silence (-96 dBFS) is normal and ignored by alerts.',
  },
  DeadAir: {
    label: 'Dead mic',
    summary: 'PTT keyed, but samples are zero (-96 dBFS). Triggers a dead-air fault.',
  },
  ClippingSpike: {
    label: 'Clipping',
    summary: 'Input hitting 0 dBFS ceiling. Triggers a clipping warning.',
  },
  JitterUnderrun: {
    label: 'Packet drop',
    summary: 'Consumer drains faster than packets arrive; ring buffer pads gaps with silence.',
  },
};

export default function App() {
  const [data, setData] = useState<TelemetryData[]>([]);
  const [audioHistory, setAudioHistory] = useState<AudioFrameMetrics[]>([]);
  const [incidents, setIncidents] = useState<StreamIncident[]>([]);
  const [simMode, setSimMode] = useState<SimMode>('NormalVoice');
  const [hubStatus, setHubStatus] = useState<'Connecting' | 'Connected' | 'Reconnecting' | 'Disconnected'>('Connecting');
  const [soapResult, setSoapResult] = useState<string>('');
  const [error, setError] = useState<string>('');

  useEffect(() => {
    let isMounted = true;

    // 1. Initial REST hydration (EF Core backed)
    Promise.all([
      fetch(`${API_BASE}/api/telemetry`, { cache: 'no-store' }).then((r) => r.json()),
      fetch(`${API_BASE}/api/incidents`, { cache: 'no-store' }).then((r) => r.json()),
      fetch(`${API_BASE}/api/audio/mode`, { cache: 'no-store' }).then((r) => r.json()),
    ])
      .then(([telemetryRows, incidentRows, modeObj]) => {
        if (!isMounted) return;
        setData(telemetryRows);
        setIncidents(incidentRows);
        if (modeObj?.mode) setSimMode(modeObj.mode as SimMode);
      })
      .catch((err) => {
        if (isMounted) setError(`Initial load failed: ${err}`);
      });

    // 2. Real-time SignalR Hub connection (replaces legacy 3-second polling)
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`${API_BASE}/hubs/telemetry`)
      .withAutomaticReconnect()
      .build();

    connection.on('ReceiveTelemetry', (record: TelemetryData) => {
      setData((prev) => [...prev.slice(-14), record]);
    });

    connection.on('ReceiveAudioMetrics', (metrics: AudioFrameMetrics) => {
      setAudioHistory((prev) => [...prev.slice(-29), metrics]);
    });

    connection.on('ReceiveIncident', (incident: StreamIncident) => {
      setIncidents((prev) => [incident, ...prev].slice(0, 20));
    });

    connection.onreconnecting(() => setHubStatus('Reconnecting'));
    connection.onreconnected(() => setHubStatus('Connected'));
    connection.onclose(() => setHubStatus('Disconnected'));

    connection
      .start()
      .then(() => {
        if (isMounted) {
          setHubStatus('Connected');
          setError('');
        }
      })
      .catch((err) => {
        if (isMounted) {
          setHubStatus('Disconnected');
          setError(`SignalR connection error: ${err}`);
        }
      });

    return () => {
      isMounted = false;
      connection.stop().catch(() => undefined);
    };
  }, []);

  const changeMode = async (mode: SimMode) => {
    setSimMode(mode);
    try {
      await fetch(`${API_BASE}/api/audio/mode/${mode}`, { method: 'POST' });
    } catch (err) {
      setError(`Failed to set mode: ${err}`);
    }
  };

  const triggerWcfSoapBridge = async () => {
    setSoapResult('Sending SOAP 1.1 envelope to CoreWCF...');
    try {
      const res = await fetch(`${API_BASE}/api/legacy/soap-demo`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          channelKey: 'wcf-dispatch-01',
          activeNodes: Math.floor(20 + Math.random() * 40),
          statusText: 'WCF_LEGACY_BRIDGE_OK',
          reportedRmsDb: -16.4,
        }),
      });
      const json = await res.json();
      setSoapResult(`HTTP ${json.statusCode} from ${json.endpoint}\n\n${json.soapResponse}`);
    } catch (err) {
      setSoapResult(`SOAP request error: ${err}`);
    }
  };

  const latestTelemetry = data.length > 0 ? data[data.length - 1] : null;
  const latestAudio = audioHistory.length > 0 ? audioHistory[audioHistory.length - 1] : null;
  const meterPercent = latestAudio ? Math.max(0, Math.min(100, ((latestAudio.rmsDb + 96) / 96) * 100)) : 0;

  return (
    <div className="min-h-screen bg-gray-950 p-6 md:p-8 font-sans text-gray-100">
      <div className="max-w-6xl mx-auto space-y-6">
        <header className="flex flex-wrap items-center justify-between gap-4 border-b border-gray-800 pb-4">
          <div>
            <h1 className="text-2xl font-bold text-white tracking-tight">Channel Monitor</h1>
            <p className="text-gray-400 text-xs mt-0.5">
              dispatch-ch1 · 16 kHz PCM · SignalR
            </p>
          </div>
          <div className="flex items-center gap-2 px-3 py-1.5 rounded-full bg-gray-900 border border-gray-700 text-xs font-medium">
            <span
              className={`w-2.5 h-2.5 rounded-full ${
                hubStatus === 'Connected' ? 'bg-green-400 animate-pulse' : 'bg-amber-400'
              }`}
            />
            <span>{hubStatus}</span>
          </div>
        </header>

        {error && (
          <div className="bg-red-900/40 text-red-200 p-4 rounded-lg border border-red-700 text-sm">
            {error}
          </div>
        )}

        {/* Controls */}
        <section className="bg-gray-900 rounded-xl p-4 border border-gray-800 space-y-2.5">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="flex flex-wrap items-center gap-2">
              <span className="text-xs font-semibold uppercase text-gray-400 mr-1">Signal</span>
              {(Object.keys(MODE_META) as SimMode[]).map((mode) => (
                <button
                  key={mode}
                  onClick={() => changeMode(mode)}
                  className={`px-3 py-1.5 rounded-lg text-xs font-semibold transition ${
                    simMode === mode
                      ? 'bg-blue-600 text-white shadow'
                      : 'bg-gray-800 text-gray-300 hover:bg-gray-700'
                  }`}
                >
                  {MODE_META[mode].label}
                </button>
              ))}
            </div>
            <button
              onClick={triggerWcfSoapBridge}
              className="px-3.5 py-1.5 rounded-lg text-xs font-semibold bg-emerald-600 hover:bg-emerald-500 text-white transition"
            >
              Send SOAP packet
            </button>
          </div>

          <p className="text-xs text-gray-400 border-t border-gray-800/80 pt-2">
            {MODE_META[simMode].summary}
          </p>
        </section>

        {/* Metrics */}
        <section className="grid grid-cols-1 md:grid-cols-4 gap-4">
          <div className="bg-gray-900 rounded-xl p-5 border border-gray-800">
            <h3 className="text-gray-400 text-xs font-semibold uppercase mb-1">RMS Level</h3>
            <p className="text-3xl font-bold text-cyan-400">
              {latestAudio ? `${latestAudio.rmsDb.toFixed(1)} dBFS` : '--'}
            </p>
            <div className="w-full h-2 bg-gray-800 rounded-full mt-3 overflow-hidden">
              <div
                className={`h-full transition-all duration-150 ${
                  latestAudio?.isClipping
                    ? 'bg-red-500'
                    : simMode === 'PttIdle'
                    ? 'bg-gray-600'
                    : latestAudio?.isDeadAir
                    ? 'bg-amber-500'
                    : 'bg-cyan-400'
                }`}
                style={{ width: `${meterPercent}%` }}
              />
            </div>
          </div>

          <div className="bg-gray-900 rounded-xl p-5 border border-gray-800">
            <h3 className="text-gray-400 text-xs font-semibold uppercase mb-1">Channel State</h3>
            <p
              className={`text-2xl font-bold mt-1 ${
                latestAudio?.isClipping
                  ? 'text-red-400'
                  : simMode === 'PttIdle'
                  ? 'text-slate-300'
                  : latestAudio?.isDeadAir
                  ? 'text-amber-400'
                  : 'text-emerald-400'
              }`}
            >
              {latestAudio
                ? latestAudio.isClipping
                  ? 'Clipping'
                  : simMode === 'PttIdle'
                  ? 'PTT idle'
                  : latestAudio.isDeadAir
                  ? 'Dead air'
                  : 'Transmitting'
                : 'Waiting'}
            </p>
            <p className="text-xs text-gray-500 mt-2">
              Peak {latestAudio ? `${latestAudio.peakDb.toFixed(1)} dBFS` : '--'} · {latestAudio?.totalFramesProcessed ?? 0} frames
            </p>
          </div>

          <div className="bg-gray-900 rounded-xl p-5 border border-gray-800">
            <h3 className="text-gray-400 text-xs font-semibold uppercase mb-1">Ring Buffer</h3>
            <p className="text-2xl font-bold text-white mt-1">
              {latestAudio ? `${Math.round(latestAudio.bufferFillRatio * 100)}% full` : '--'}
            </p>
            <p className="text-xs text-gray-400 mt-2">
              <span className="text-amber-400 font-semibold">{latestAudio?.underrunCount ?? 0}</span> underruns ·{' '}
              <span className="text-red-400 font-semibold">{latestAudio?.overrunCount ?? 0}</span> overruns
            </p>
          </div>

          <div className="bg-gray-900 rounded-xl p-5 border border-gray-800">
            <h3 className="text-gray-400 text-xs font-semibold uppercase mb-1">WCF Bridge</h3>
            <p className="text-2xl font-bold text-emerald-400 mt-1">
              {latestTelemetry ? `${latestTelemetry.activePlayers} units` : 'Idle'}
            </p>
            <p className="text-xs text-gray-400 mt-2 truncate">
              {latestTelemetry ? latestTelemetry.serverStatus : 'No SOAP packets yet'}
            </p>
          </div>
        </section>

        {/* Chart */}
        <section className="bg-gray-900 rounded-xl p-5 border border-gray-800">
          <h3 className="text-gray-400 text-xs font-semibold uppercase mb-4">RMS &amp; Peak (dBFS)</h3>
          <div className="h-64 w-full">
            <ResponsiveContainer width="100%" height="100%">
              <LineChart data={audioHistory}>
                <CartesianGrid strokeDasharray="3 3" stroke="#1F2937" vertical={false} />
                <XAxis dataKey="totalFramesProcessed" stroke="#6B7280" tick={{ fill: '#9CA3AF', fontSize: 11 }} />
                <YAxis domain={[-96, 0]} stroke="#6B7280" tick={{ fill: '#9CA3AF', fontSize: 11 }} />
                <Tooltip
                  contentStyle={{ backgroundColor: '#111827', border: '1px solid #374151', borderRadius: '8px', color: '#fff' }}
                />
                <Line type="monotone" dataKey="rmsDb" stroke="#22D3EE" strokeWidth={2.5} dot={false} isAnimationActive={false} />
                <Line type="monotone" dataKey="peakDb" stroke="#F59E0B" strokeWidth={1.5} dot={false} isAnimationActive={false} />
              </LineChart>
            </ResponsiveContainer>
          </div>
        </section>

        {/* Logs */}
        <section className="grid grid-cols-1 lg:grid-cols-2 gap-6">
          <div className="bg-gray-900 rounded-xl p-5 border border-gray-800">
            <h3 className="text-gray-400 text-xs font-semibold uppercase mb-3">Incident Log</h3>
            <div className="space-y-2 max-h-64 overflow-y-auto pr-1">
              {incidents.length === 0 && (
                <p className="text-sm text-gray-500">No incidents recorded.</p>
              )}
              {incidents.map((inc, idx) => (
                <div key={`${inc.id}-${idx}`} className="p-3 rounded-lg bg-gray-950 border border-gray-800 text-xs">
                  <div className="flex justify-between items-center mb-1">
                    <span className="font-semibold text-white">
                      [{inc.severity}] {inc.incidentType} · {inc.channelKey}
                    </span>
                    <span className="text-gray-500">{new Date(inc.createdAtUtc).toLocaleTimeString()}</span>
                  </div>
                  <p className="text-gray-400">{inc.details}</p>
                </div>
              ))}
            </div>
          </div>

          <div className="bg-gray-900 rounded-xl p-5 border border-gray-800">
            <h3 className="text-gray-400 text-xs font-semibold uppercase mb-3">SOAP Wire Log</h3>
            <pre className="text-xs text-emerald-300 bg-gray-950 p-3 rounded-lg border border-gray-800 h-64 overflow-auto whitespace-pre-wrap">
              {soapResult || 'Click "Send SOAP packet" to inspect the XML exchange.'}
            </pre>
          </div>
        </section>
      </div>
    </div>
  );
}
