import { socket } from '@kit.NetworkKit';
import { MessageCodec } from '../protocol/MessageCodec';
import { MessageType } from '../protocol/MessageType';
import { HandshakePayload } from '../protocol/Payloads';
import { FLYSCREEN_VERSION } from '../FlyScreenVersion';
import { DiscoveryService } from './DiscoveryService';
import { PeerSession } from './PeerSession';

export class ConnectionManager {
  securityKey: string;
  private localMachine: string;
  private deviceClass: string;
  private platform: string;
  private caps: string[];
  private tcpPort: number;
  discovery: DiscoveryService;
  private sessions: Map<string, PeerSession> = new Map();
  private server: socket.TCPSocketServer | null = null;
  bindIp: string = '0.0.0.0';
  isListening: boolean = false;

  onStatus: ((msg: string) => void) | null = null;
  onPeersUpdated: (() => void) | null = null;

  constructor(
    securityKey: string,
    localMachine: string,
    deviceClass: string,
    platform: string,
    caps: string[],
    tcpPort: number = 15155,
    udpPort: number = 15156
  ) {
    this.securityKey = securityKey;
    this.localMachine = localMachine;
    this.deviceClass = deviceClass;
    this.platform = platform;
    this.caps = caps;
    this.tcpPort = tcpPort;
    this.discovery = new DiscoveryService(localMachine, tcpPort, udpPort);
  }

  async start(localIp: string): Promise<void> {
    this.bindIp = localIp;
    this.discovery.onPeersUpdated = () => {
      if (this.onPeersUpdated) {
        this.onPeersUpdated();
      }
    };
    await this.discovery.start(localIp);
    await this.startListener();
    this.emit(`发现已启动，监听 ${this.tcpPort}`);
  }

  async stop(): Promise<void> {
    await this.discovery.stop();
    if (this.server) {
      try {
        this.server.close();
      } catch (_e) {
        // ignore
      }
      this.server = null;
    }
    this.isListening = false;
    this.sessions.forEach((s) => s.close());
    this.sessions.clear();
  }

  async connectTo(peerName: string, ip: string, port: number = this.tcpPort): Promise<void> {
    this.emit(`正在连接 ${peerName} (${ip})…`);
    try {
      const tcp = socket.constructTCPSocketInstance();
      await tcp.connect({ address: { address: ip, port: port, family: 1 }, timeout: 5000 });
      const session = new PeerSession(tcp, peerName);
      this.wire(session, true, peerName);
      session.start();
      const hs = this.buildHandshake(false);
      await session.send(MessageCodec.encodeJson(MessageType.Handshake, hs));
    } catch (e) {
      this.emit(`连接失败: ${JSON.stringify(e)}`);
    }
  }

  sendToActive(frame: Uint8Array): void {
    this.sessions.forEach((s) => {
      if (s.authenticated) {
        s.send(frame);
      }
    });
  }

  hasAuthenticatedPeer(): boolean {
    let ok = false;
    this.sessions.forEach((s) => {
      if (s.authenticated) {
        ok = true;
      }
    });
    return ok;
  }

  private async startListener(): Promise<void> {
    try {
      const server = socket.constructTCPSocketServerInstance();
      this.server = server;
      await server.listen({ address: '0.0.0.0', port: this.tcpPort, family: 1 });
      this.isListening = true;
      server.on('connect', async (client: socket.TCPSocket) => {
        const session = new PeerSession(client);
        this.wire(session, false, null);
        session.start();
      });
    } catch (e) {
      this.isListening = false;
      this.emit(`监听失败: ${JSON.stringify(e)}`);
    }
  }

  private wire(session: PeerSession, outbound: boolean, expectedPeer: string | null): void {
    session.setHandlers(async (type: number, payload: Uint8Array) => {
      if (type === MessageType.HandshakeAck) {
        const ack = MessageCodec.decodeJson<HandshakePayload>(payload);
        if (ack && ack.SecurityKey.trim() === this.securityKey.trim()) {
          session.authenticated = true;
          session.peerName = expectedPeer ?? ack.MachineName;
          this.sessions.set(session.peerName, session);
          this.emit(`已连接 ${session.peerName}`);
        } else {
          session.close();
          this.emit('握手密钥不匹配');
        }
        return;
      }
      if (type === MessageType.Handshake && !outbound) {
        const hs = MessageCodec.decodeJson<HandshakePayload>(payload);
        if (!hs || hs.SecurityKey.trim() !== this.securityKey.trim()) {
          session.close();
          return;
        }
        session.peerName = hs.MachineName;
        session.authenticated = true;
        this.sessions.set(hs.MachineName, session);
        const ack = this.buildHandshake(true);
        await session.send(MessageCodec.encodeJson(MessageType.HandshakeAck, ack));
        this.emit(`入站已认证 ${hs.MachineName}`);
        return;
      }
      if (type === MessageType.Heartbeat) {
        await session.send(MessageCodec.encode(MessageType.HeartbeatAck));
      }
    }, () => {
      const name = session.peerName;
      if (name) {
        this.sessions.delete(name);
      }
      this.emit(name ? `${name} 已断开` : '连接断开');
    });
  }

  private buildHandshake(isReply: boolean): HandshakePayload {
    const hs = new HandshakePayload();
    hs.MachineName = this.localMachine;
    hs.SecurityKey = this.securityKey;
    hs.LocalIp = this.bindIp;
    hs.IsReply = isReply;
    hs.Platform = this.platform;
    hs.DeviceClass = this.deviceClass;
    hs.Caps = this.caps;
    hs.AppVersion = FLYSCREEN_VERSION;
    return hs;
  }

  private emit(msg: string): void {
    if (this.onStatus) {
      this.onStatus(msg);
    }
  }
}
