import {
  HarmonyAppHost,
  HarmonyHostMode
} from 'common';

@Entry
@Component
struct Index {
  @State statusText: string = '未连接';
  @State keyText: string = '';
  @State peerText: string = '';
  @State peers: string[] = [];
  @State info: string = '';
  private host: HarmonyAppHost = HarmonyAppHost.get(HarmonyHostMode.Mobile);
  private lastX: number = 0;
  private lastY: number = 0;
  private moved: boolean = false;

  aboutToAppear(): void {
    this.info =
      `本机 ${this.host.localMachine} · ${this.host.deviceClass} · Caps=${this.host.caps.join(',')}`;
    this.keyText = this.host.securityKey;
    this.peerText = this.host.targetPeer;
    this.host.onStatus = (m: string) => {
      this.statusText = m;
    };
    this.host.onPeersUpdated = () => {
      this.peers = this.host.discoveredNames();
    };
    this.host.start();
  }

  build() {
    Column({ space: 12 }) {
      Text('玄鸟')
        .fontSize(22)
        .fontWeight(FontWeight.Bold)
        .fontColor($r('app.color.text'))
        .width('100%')

      Text(this.info)
        .fontSize(12)
        .fontColor($r('app.color.muted'))
        .width('100%')

      Text('安全密钥')
        .fontSize(15)
        .fontWeight(FontWeight.Medium)
        .fontColor($r('app.color.text'))
        .width('100%')
      Text('与电脑端「基础设置」中的密钥一致（最多 4 台）。')
        .fontSize(12)
        .fontColor($r('app.color.muted'))
        .width('100%')
      TextInput({ text: this.keyText, placeholder: '安全密钥' })
        .type(InputType.Password)
        .onChange((v: string) => {
          this.keyText = v;
        })
        .backgroundColor($r('app.color.card_bg'))
        .width('100%')

      Text('目标电脑')
        .fontSize(15)
        .fontWeight(FontWeight.Medium)
        .fontColor($r('app.color.text'))
        .width('100%')
      Text('对应电脑端「电脑布局」中的机器名；也可从下方发现列表选择。')
        .fontSize(12)
        .fontColor($r('app.color.muted'))
        .width('100%')
      TextInput({ text: this.peerText, placeholder: '机器名' })
        .onChange((v: string) => {
          this.peerText = v;
        })
        .backgroundColor($r('app.color.card_bg'))
        .width('100%')

      if (this.peers.length > 0) {
        Text(`局域网发现: ${this.peers.join('、')}`)
          .fontSize(12)
          .fontColor($r('app.color.muted'))
          .width('100%')
      }

      Button('应用')
        .width('100%')
        .backgroundColor($r('app.color.accent'))
        .fontColor(Color.White)
        .onClick(() => {
          this.host.applyAndConnect(this.keyText, this.peerText);
        })

      Text(this.statusText)
        .fontSize(12)
        .fontColor($r('app.color.muted'))
        .width('100%')

      Text('触控板：单指移动 · 点击轻触')
        .fontSize(12)
        .fontColor($r('app.color.muted'))
        .width('100%')

      Column()
        .width('100%')
        .layoutWeight(1)
        .backgroundColor($r('app.color.pad'))
        .borderRadius(8)
        .gesture(
          PanGesture({ fingers: 1 })
            .onActionStart((e: GestureEvent) => {
              this.lastX = e.offsetX;
              this.lastY = e.offsetY;
              this.moved = false;
            })
            .onActionUpdate((e: GestureEvent) => {
              const dx = e.offsetX - this.lastX;
              const dy = e.offsetY - this.lastY;
              if (Math.abs(dx) > 0.5 || Math.abs(dy) > 0.5) {
                this.host.sendMoveRelative(dx, dy);
                this.lastX = e.offsetX;
                this.lastY = e.offsetY;
                this.moved = true;
              }
            })
            .onActionEnd(() => {
              if (!this.moved) {
                this.host.sendClick(0);
              }
            })
        )

      Row({ space: 8 }) {
        Button('左键')
          .layoutWeight(1)
          .onClick(() => this.host.sendClick(0))
        Button('右键')
          .layoutWeight(1)
          .onClick(() => this.host.sendClick(1))
      }
      .width('100%')
    }
    .padding(16)
    .width('100%')
    .height('100%')
    .backgroundColor($r('app.color.page_bg'))
  }
}
