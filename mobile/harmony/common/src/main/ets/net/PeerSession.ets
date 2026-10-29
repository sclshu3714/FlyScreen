import { socket } from '@kit.NetworkKit';
import { MessageCodec } from '../protocol/MessageCodec';

export type MessageHandler = (type: number, payload: Uint8Array) => void;
export type VoidHandler = () => void;

/** 单条 TCP 会话。 */
export class PeerSession {
  peerName: string = '';
  authenticated: boolean = false;

  private tcp: socket.TCPSocket;
  private onMessage: MessageHandler | null = null;
  private onDisconnected: VoidHandler | null = null;
  private closed: boolean = false;

  private headerBuf: number[] = [];
  private payloadBuf: number[] = [];
  private needLen: number = -1;
  private pendingType: number = 0;

  constructor(tcp: socket.TCPSocket, peerNameHint: string = '') {
    this.tcp = tcp;
    this.peerName = peerNameHint;
  }

  setHandlers(onMessage: MessageHandler, onDisconnected: VoidHandler): void {
    this.onMessage = onMessage;
    this.onDisconnected = onDisconnected;
  }

  start(): void {
    this.tcp.on('message', (value: socket.SocketMessageInfo) => {
      if (this.closed) {
        return;
      }
      this.feed(new Uint8Array(value.message));
    });
    this.tcp.on('close', () => {
      this.raiseClose();
    });
    this.tcp.on('error', (_err: Error) => {
      this.raiseClose();
    });
  }

  async send(frame: Uint8Array): Promise<void> {
    if (this.closed) {
      return;
    }
    try {
      const copy = frame.buffer.slice(frame.byteOffset, frame.byteOffset + frame.byteLength);
      await this.tcp.send({ data: copy });
    } catch (_e) {
      this.raiseClose();
    }
  }

  close(): void {
    if (this.closed) {
      return;
    }
    this.closed = true;
    try {
      this.tcp.close();
    } catch (_e) {
      // ignore
    }
  }

  private feed(chunk: Uint8Array): void {
    for (let i = 0; i < chunk.length; i++) {
      const b = chunk[i];
      if (this.needLen < 0) {
        this.headerBuf.push(b);
        if (this.headerBuf.length === MessageCodec.HEADER_SIZE) {
          this.pendingType = this.headerBuf[0];
          const len = (this.headerBuf[1] & 0xff) |
            ((this.headerBuf[2] & 0xff) << 8) |
            ((this.headerBuf[3] & 0xff) << 16) |
            ((this.headerBuf[4] & 0xff) << 24);
          this.headerBuf = [];
          if (len < 0 || len > MessageCodec.MAX_PAYLOAD) {
            this.raiseClose();
            return;
          }
          this.needLen = len;
          this.payloadBuf = [];
          if (len === 0) {
            this.dispatch(this.pendingType, new Uint8Array(0));
            this.needLen = -1;
          }
        }
      } else {
        this.payloadBuf.push(b);
        if (this.payloadBuf.length >= this.needLen) {
          const payload = new Uint8Array(this.payloadBuf);
          this.dispatch(this.pendingType, payload);
          this.needLen = -1;
          this.payloadBuf = [];
        }
      }
    }
  }

  private dispatch(type: number, payload: Uint8Array): void {
    if (this.onMessage) {
      this.onMessage(type, payload);
    }
  }

  private raiseClose(): void {
    if (this.closed) {
      if (this.onDisconnected) {
        // already closed
      }
      return;
    }
    this.closed = true;
    if (this.onDisconnected) {
      this.onDisconnected();
    }
  }
}
