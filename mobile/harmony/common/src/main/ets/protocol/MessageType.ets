/** 与 Core MessageType 数值一致。 */
export class MessageType {
  static readonly Handshake: number = 1;
  static readonly HandshakeAck: number = 2;
  static readonly Heartbeat: number = 3;
  static readonly HeartbeatAck: number = 4;
  static readonly MouseMove: number = 10;
  static readonly MouseButton: number = 11;
  static readonly MouseWheel: number = 12;
  static readonly KeyEvent: number = 20;
  static readonly ClipboardText: number = 30;
  static readonly SwitchFocus: number = 50;
  static readonly LayoutSync: number = 61;
  static readonly CursorOwner: number = 62;
}
