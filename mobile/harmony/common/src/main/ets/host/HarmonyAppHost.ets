import { connection } from '@kit.NetworkKit';
import { ConnectionManager } from '../net/ConnectionManager';
import { MessageCodec } from '../protocol/MessageCodec';
import { MessageType } from '../protocol/MessageType';
import {
  MouseButtonPayload,
  MouseMovePayload,
  MouseWheelPayload,
  PeerCaps,
  PeerDeviceClass,
  PeerPlatform
} from '../protocol/Payloads';
import { buildLocalMachineName, detectDeviceClass, detectHostMode, HarmonyHostMode } from './DeviceDetector';

export { HarmonyHostMode };

/**
 * 鸿蒙业务宿主：手机/平板与电脑共用连接层；按 DeviceClass 宣告不同 Caps。
 */
export class HarmonyAppHost {
  static instance: HarmonyAppHost | null = null;

  readonly mode: HarmonyHostMode;
  readonly deviceClass: string;
  readonly localMachine: string;
  caps: string[];
  connections: ConnectionManager;
  securityKey: string = 'changeme';
  targetPeer: string = '';
  onStatus: ((msg: string) => void) | null = null;
  onPeersUpdated: (() => void) | null = null;

  private constructor(forceMode: HarmonyHostMode | null) {
    this.mode = forceMode ?? detectHostMode();
    this.deviceClass = forceMode === HarmonyHostMode.Desktop
      ? PeerDeviceClass.Desktop
      : (forceMode === HarmonyHostMode.Mobile
        ? (detectDeviceClass() === PeerDeviceClass.Tablet ? PeerDeviceClass.Tablet : PeerDeviceClass.Phone)
        : detectDeviceClass());

    if (this.deviceClass === PeerDeviceClass.Desktop) {
      this.caps = PeerCaps.HarmonyDesktop;
    } else if (this.deviceClass === PeerDeviceClass.Unknown) {
      this.caps = PeerCaps.HarmonyMobile;
    } else {
      this.caps = PeerCaps.HarmonyMobile;
    }

    this.localMachine = buildLocalMachineName();
    this.connections = new ConnectionManager(
      this.securityKey,
      this.localMachine,
      this.deviceClass,
      PeerPlatform.HarmonyOS,
      this.caps
    );
  }

  static get(forceMode: HarmonyHostMode | null = null): HarmonyAppHost {
    if (HarmonyAppHost.instance) {
      return HarmonyAppHost.instance;
    }
    HarmonyAppHost.instance = new HarmonyAppHost(forceMode);
    return HarmonyAppHost.instance;
  }

  /** 电脑端若注入/捕获 API 不可用，降级 Caps（禁止因此改 Win 语义）。 */
  degradeDesktopCaps(): void {
    if (this.deviceClass !== PeerDeviceClass.Desktop) {
      return;
    }
    this.caps = PeerCaps.HarmonyDesktopDegraded;
    this.connections = new ConnectionManager(
      this.securityKey,
      this.localMachine,
      this.deviceClass,
      PeerPlatform.HarmonyOS,
      this.caps
    );
    this.bindCallbacks();
  }

  async start(): Promise<void> {
    this.bindCallbacks();
    const ip = await this.resolveLocalIpv4();
    await this.connections.start(ip);
  }

  async applyAndConnect(key: string, peer: string): Promise<void> {
    this.securityKey = key.trim();
    this.targetPeer = peer.trim();
    this.connections.securityKey = this.securityKey;
    await this.connections.stop();
    await this.start();
    const found = this.connections.discovery.getPeers()
      .find(p => p.machineName.toLowerCase() === peer.toLowerCase());
    if (found) {
      await this.connections.connectTo(found.machineName, found.ip, found.tcpPort);
    } else if (peer.length > 0) {
      this.emitStatus(`已保存；等待发现 ${peer}…`);
    }
  }

  sendMoveRelative(dx: number, dy: number): void {
    const p = new MouseMovePayload();
    p.Relative = true;
    p.Dx = Math.round(dx);
    p.Dy = Math.round(dy);
    this.connections.sendToActive(MessageCodec.encodeJson(MessageType.MouseMove, p));
  }

  sendClick(button: number): void {
    const down = new MouseButtonPayload();
    down.Button = button;
    down.Down = true;
    const up = new MouseButtonPayload();
    up.Button = button;
    up.Down = false;
    this.connections.sendToActive(MessageCodec.encodeJson(MessageType.MouseButton, down));
    this.connections.sendToActive(MessageCodec.encodeJson(MessageType.MouseButton, up));
  }

  sendWheel(delta: number): void {
    const p = new MouseWheelPayload();
    p.Delta = delta;
    this.connections.sendToActive(MessageCodec.encodeJson(MessageType.MouseWheel, p));
  }

  discoveredNames(): string[] {
    return this.connections.discovery.getPeers().map(p => p.machineName);
  }

  private bindCallbacks(): void {
    this.connections.onStatus = (m: string) => this.emitStatus(m);
    this.connections.onPeersUpdated = () => {
      if (this.onPeersUpdated) {
        this.onPeersUpdated();
      }
    };
  }

  private emitStatus(msg: string): void {
    if (this.onStatus) {
      this.onStatus(msg);
    }
  }

  private async resolveLocalIpv4(): Promise<string> {
    try {
      const handle = connection.getDefaultNetSync();
      const info = connection.getConnectionPropertiesSync(handle);
      if (info.linkAddresses) {
        for (let i = 0; i < info.linkAddresses.length; i++) {
          const addr = info.linkAddresses[i].address?.address;
          if (addr && addr.indexOf(':') < 0 && addr !== '127.0.0.1') {
            return addr;
          }
        }
      }
    } catch (_e) {
      // ignore
    }
    return '0.0.0.0';
  }
}
