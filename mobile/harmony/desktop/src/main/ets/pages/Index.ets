import { HarmonyAppHost, HarmonyHostMode } from 'common';

/**
 * W5b 鸿蒙电脑壳：设置 / 发现 / 连接。
 * 全局键鼠捕获与注入需系统能力，后续迭代；API 不足时 degradeDesktopCaps。
 */
@Entry
@Component
struct Index {
  @State statusText: string = '未连接';
  @State keyText: string = '';
  @State slot0: string = '';
  @State slot1: string = '';
  @State slot2: string = '';
  @State slot3: string = '';
  @State peers: string[] = [];
  @State info: string = '';
  @State degraded: boolean = false;
  private host: HarmonyAppHost = HarmonyAppHost.get(HarmonyHostMode.Desktop);

  aboutToAppear(): void {
    this.info =
      `本机 ${this.host.localMachine} · Desktop · Caps=${this.host.caps.join(',')}`;
    this.keyText = this.host.securityKey;
    this.slot0 = this.host.localMachine;
    this.host.onStatus = (m: string) => {
      this.statusText = m;
    };
    this.host.onPeersUpdated = () => {
      this.peers = this.host.discoveredNames();
    };
    this.host.start();
  }

  build() {
    Scroll() {
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

        Text('界面分区与 Windows「基础设置」一致：安全密钥 + 电脑布局（最多 4 台）。')
          .fontSize(12)
          .fontColor($r('app.color.muted'))
          .width('100%')

        Text('安全密钥')
          .fontSize(15)
          .fontWeight(FontWeight.Medium)
          .fontColor($r('app.color.text'))
          .width('100%')
        Text('多台电脑必须使用相同的安全密钥才能连接。')
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

        Text('电脑布局')
          .fontSize(15)
          .fontWeight(FontWeight.Medium)
          .fontColor($r('app.color.text'))
          .width('100%')
        TextInput({ text: this.slot0, placeholder: '最左 / 左上' })
          .onChange((v: string) => {
            this.slot0 = v;
          })
          .backgroundColor($r('app.color.card_bg'))
          .width('100%')
        TextInput({ text: this.slot1, placeholder: '中左 / 右上' })
          .onChange((v: string) => {
            this.slot1 = v;
          })
          .backgroundColor($r('app.color.card_bg'))
          .width('100%')
        TextInput({ text: this.slot2, placeholder: '中右 / 左下' })
          .onChange((v: string) => {
            this.slot2 = v;
          })
          .backgroundColor($r('app.color.card_bg'))
          .width('100%')
        TextInput({ text: this.slot3, placeholder: '最右 / 右下' })
          .onChange((v: string) => {
            this.slot3 = v;
          })
          .backgroundColor($r('app.color.card_bg'))
          .width('100%')

        if (this.peers.length > 0) {
          Text(`发现: ${this.peers.join('、')}`)
            .fontSize(12)
            .fontColor($r('app.color.muted'))
            .width('100%')
        }

        Row({ space: 8 }) {
          Button('应用')
            .layoutWeight(1)
            .onClick(() => {
              const peer = this.slot1.length > 0 ? this.slot1 :
                (this.peers.length > 0 ? this.peers[0] : '');
              this.host.applyAndConnect(this.keyText, peer);
            })
          Button(this.degraded ? '已降级 Caps' : '降级桌面 Caps')
            .enabled(!this.degraded)
            .onClick(() => {
              this.host.degradeDesktopCaps();
              this.degraded = true;
              this.info =
                `本机 ${this.host.localMachine} · Desktop(降级) · Caps=${this.host.caps.join(',')}`;
              this.host.start();
            })
        }
        .width('100%')

        Text(this.statusText)
          .fontSize(12)
          .fontColor($r('app.color.muted'))
          .width('100%')
      }
      .padding(20)
      .width('100%')
    }
    .width('100%')
    .height('100%')
    .backgroundColor($r('app.color.page_bg'))
  }
}
