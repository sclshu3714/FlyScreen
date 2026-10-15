export { MessageType } from './src/main/ets/protocol/MessageType';
export { MessageCodec } from './src/main/ets/protocol/MessageCodec';
export {
  HandshakePayload,
  MouseMovePayload,
  MouseButtonPayload,
  MouseWheelPayload,
  DiscoveryPacket,
  PeerCaps,
  PeerPlatform,
  PeerDeviceClass
} from './src/main/ets/protocol/Payloads';

export { DiscoveryService } from './src/main/ets/net/DiscoveryService';
export { PeerSession } from './src/main/ets/net/PeerSession';
export { ConnectionManager } from './src/main/ets/net/ConnectionManager';

export { HarmonyAppHost, HarmonyHostMode } from './src/main/ets/host/HarmonyAppHost';
export { detectDeviceClass, detectHostMode } from './src/main/ets/host/DeviceDetector';
export { FLYSCREEN_VERSION, FLYSCREEN_VERSION_CODE } from './src/main/ets/FlyScreenVersion';
