import { socket } from '@kit.NetworkKit';
import { MessageCodec } from '../protocol/MessageCodec';
import { DiscoveryPacket } from '../protocol/Payloads';

export class DiscoveredPeer {
  machineName: string = '';
  ip: string = '';
  tcpPort: number = 15155;
  lastSeenMs: number = 0;
}

/**
 * UDP 发现：载荷为原始 JSON（无 TCP 帧头），与 Core DiscoveryService 一致。
 */
export class DiscoveryService {
  private localMachine: string;
  private tcpPort: number;
  private udpPort: number;
  private localIp: string = '0.0.0.0';
  private peers: Map<string, DiscoveredPeer> = new Map();
  private udp: socket.UDPSocket | null = null;
  private timerId: number = -1;
  onPeersUpdated: (() => void) | null = null;

  constructor(localMachine: string, tcpPort: number = 15155, udpPort: number = 15156) {
    this.localMachine = localMachine;
    this.tcpPort = tcpPort;
    this.udpPort = udpPort;
  }

  async start(localIp: string): Promise<void> {
    await this.stop();
    this.localIp = localIp;
    const udp = socket.constructUDPSocketInstance();
    this.udp = udp;
    await udp.bind({ address: '0.0.0.0', port: this.udpPort, family: 1 });

    udp.on('message', (value: socket.SocketMessageInfo) => {
      try {
        const bytes = new Uint8Array(value.message);
        const pkt = MessageCodec.decodeJson<DiscoveryPacket>(bytes);
        if (!pkt || !pkt.MachineName) {
          return;
        }
        if (pkt.MachineName.toLowerCase() === this.localMachine.toLowerCase()) {
          return;
        }
        const remote = value.remoteInfo;
        const ip = (pkt.Ip && pkt.Ip.length > 0) ? pkt.Ip : (remote?.address ?? '');
        if (!ip) {
          return;
        }
        const peer = new DiscoveredPeer();
        peer.machineName = pkt.MachineName;
        peer.ip = ip;
        peer.tcpPort = pkt.TcpPort > 0 ? pkt.TcpPort : this.tcpPort;
        peer.lastSeenMs = Date.now();
        this.peers.set(pkt.MachineName, peer);
        if (this.onPeersUpdated) {
          this.onPeersUpdated();
        }
      } catch (_e) {
        // ignore
      }
    });

    this.timerId = setInterval(() => {
      this.announce();
      this.prune();
    }, 2000);
  }

  getPeers(): DiscoveredPeer[] {
    const list: DiscoveredPeer[] = [];
    this.peers.forEach((v) => list.push(v));
    return list.sort((a, b) => a.machineName.localeCompare(b.machineName));
  }

  async stop(): Promise<void> {
    if (this.timerId >= 0) {
      clearInterval(this.timerId);
      this.timerId = -1;
    }
    if (this.udp) {
      try {
        this.udp.close();
      } catch (_e) {
        // ignore
      }
      this.udp = null;
    }
  }

  private async announce(): Promise<void> {
    if (!this.udp) {
      return;
    }
    const pkt = new DiscoveryPacket();
    pkt.MachineName = this.localMachine;
    pkt.Ip = this.localIp;
    pkt.TcpPort = this.tcpPort;
    const payload = MessageCodec.encodeDiscoveryJson(pkt);
    try {
      await this.udp.send({
        data: payload.buffer.slice(payload.byteOffset, payload.byteOffset + payload.byteLength),
        address: { address: '255.255.255.255', port: this.udpPort, family: 1 }
      });
    } catch (_e) {
      // ignore
    }
  }

  private prune(): void {
    const cutoff = Date.now() - 10000;
    const keys: string[] = [];
    this.peers.forEach((v, k) => {
      if (v.lastSeenMs < cutoff) {
        keys.push(k);
      }
    });
    keys.forEach((k) => this.peers.delete(k));
  }
}
