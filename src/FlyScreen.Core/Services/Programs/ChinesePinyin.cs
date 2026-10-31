using System.Text;

namespace FlyScreen.Services.Programs;

/// <summary>项目名 → 拼音 slug（用于 com.xuanniao.*）。覆盖常用汉字，未知字跳过。</summary>
public static class ChinesePinyin
{
    public static string ToSlug(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "app";
        var sb = new StringBuilder();
        foreach (var ch in name.Trim())
        {
            if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                sb.Append(char.ToLowerInvariant(ch));
                continue;
            }
            if (ch is '_' or '-' or '.')
            {
                if (sb.Length > 0 && sb[^1] != '_')
                    sb.Append('_');
                continue;
            }
            if (Map.TryGetValue(ch, out var py) && !string.IsNullOrEmpty(py))
            {
                sb.Append(py);
                continue;
            }
        }

        var slug = sb.ToString().Trim('_');
        while (slug.Contains("__", StringComparison.Ordinal))
            slug = slug.Replace("__", "_", StringComparison.Ordinal);
        if (string.IsNullOrEmpty(slug))
            slug = "app";
        if (slug.Length > 48)
            slug = slug[..48].TrimEnd('_');
        return slug;
    }

    public static string ToNamespace(string? name)
        => "com.xuanniao." + ToSlug(name);

    private static readonly Dictionary<char, string> Map = BuildMap();

    private static Dictionary<char, string> BuildMap()
    {
        var d = new Dictionary<char, string>();
        var exact = """
            啊:a 爱:ai 安:an 八:ba 把:ba 白:bai 百:bai 班:ban 板:ban 半:ban 帮:bang
            包:bao 保:bao 报:bao 北:bei 备:bei 本:ben 比:bi 必:bi 边:bian 变:bian
            标:biao 表:biao 别:bie 并:bing 波:bo 不:bu 部:bu
            才:cai 菜:cai 参:can 藏:cang 测:ce 层:ceng 产:chan 长:chang 常:chang
            场:chang 超:chao 车:che 成:cheng 城:cheng 程:cheng 出:chu 除:chu 处:chu
            传:chuan 窗:chuang 创:chuang 次:ci 从:cong 存:cun
            大:da 带:dai 单:dan 但:dan 当:dang 导:dao 到:dao 道:dao 的:de 得:de
            地:di 第:di 点:dian 电:dian 调:diao 定:ding 东:dong 动:dong 都:dou
            读:du 度:du 短:duan 对:dui 多:duo 二:er
            发:fa 法:fa 反:fan 饭:fan 方:fang 放:fang 飞:fei 分:fen 份:fen 风:feng
            服:fu 复:fu 父:fu
            该:gai 改:gai 干:gan 感:gan 刚:gang 高:gao 告:gao 戈:ge 格:ge 个:ge
            给:gei 根:gen 更:geng 工:gong 公:gong 功:gong 共:gong 构:gou 够:gou
            古:gu 关:guan 观:guan 管:guan 光:guang 广:guang 规:gui 国:guo 果:guo
            过:guo
            还:hai 海:hai 汉:han 好:hao 号:hao 和:he 河:he 黑:hei 很:hen 红:hong
            后:hou 候:hou 乎:hu 呼:hu 湖:hu 互:hu 户:hu 花:hua 化:hua 画:hua 话:hua
            坏:huai 欢:huan 环:huan 回:hui 会:hui 活:huo 火:huo
            机:ji 基:ji 积:ji 及:ji 级:ji 集:ji 几:ji 己:ji 计:ji 记:ji 际:ji 既:ji
            加:jia 家:jia 间:jian 简:jian 见:jian 建:jian 将:jiang 讲:jiang
            交:jiao 角:jiao 教:jiao 接:jie 街:jie 节:jie 结:jie 解:jie 界:jie
            金:jin 进:jin 近:jin 经:jing 精:jing 景:jing 静:jing 九:jiu 久:jiu
            就:jiu 旧:jiu 居:ju 局:ju 据:ju 决:jue 绝:jue 军:jun
            开:kai 看:kan 康:kang 考:kao 可:ke 克:ke 客:ke 空:kong 口:kou 快:kuai
            况:kuang
            拉:la 来:lai 蓝:lan 浪:lang 老:lao 乐:le 了:le 类:lei 累:lei 冷:leng
            离:li 里:li 理:li 力:li 历:li 立:li 连:lian 联:lian 脸:lian 练:lian
            凉:liang 两:liang 亮:liang 量:liang 料:liao 列:lie 林:lin 灵:ling
            另:ling 流:liu 六:liu 龙:long 楼:lou 路:lu 录:lu 绿:lv 乱:luan 论:lun
            落:luo
            妈:ma 马:ma 吗:ma 买:mai 满:man 慢:man 忙:mang 毛:mao 么:me 没:mei
            美:mei 门:men 们:men 米:mi 面:mian 民:min 名:ming 明:ming 命:ming
            模:mo 末:mo 母:mu 目:mu 幕:mu
            那:na 内:nei 能:neng 你:ni 年:nian 念:nian 鸟:niao 您:nin 宁:ning
            牛:niu 农:nong 女:nv
            排:pai 盘:pan 旁:pang 跑:pao 配:pei 朋:peng 批:pi 皮:pi 片:pian
            票:piao 平:ping 评:ping 屏:ping 破:po 普:pu
            七:qi 期:qi 其:qi 齐:qi 起:qi 气:qi 千:qian 前:qian 钱:qian 强:qiang
            桥:qiao 切:qie 且:qie 亲:qin 青:qing 轻:qing 清:qing 情:qing 请:qing
            庆:qing 穷:qiong 求:qiu 区:qu 取:qu 去:qu 全:quan 权:quan 却:que 群:qun
            然:ran 让:rang 热:re 人:ren 认:ren 日:ri 容:rong 肉:rou 如:ru 入:ru
            三:san 色:se 山:shan 商:shang 上:shang 少:shao 绍:shao 设:she 社:she
            谁:shui 身:shen 深:shen 什:shen 生:sheng 声:sheng 省:sheng 师:shi
            十:shi 时:shi 实:shi 识:shi 史:shi 使:shi 始:shi 示:shi 世:shi 事:shi
            是:shi 适:shi 市:shi 收:shou 手:shou 守:shou 首:shou 书:shu 术:shu
            树:shu 数:shu 双:shuang 水:shui 顺:shun 说:shuo 思:si 死:si 四:si
            送:song 诉:su 算:suan 虽:sui 随:sui 岁:sui 所:suo
            他:ta 她:ta 它:ta 台:tai 太:tai 态:tai 谈:tan 汤:tang 堂:tang 逃:tao
            特:te 提:ti 题:ti 体:ti 天:tian 条:tiao 听:ting 停:ting 通:tong 同:tong
            头:tou 图:tu 土:tu 团:tuan 推:tui 退:tui
            外:wai 完:wan 玩:wan 晚:wan 万:wan 网:wang 往:wang 望:wang 危:wei
            微:wei 为:wei 维:wei 伟:wei 位:wei 文:wen 问:wen 我:wo 无:wu 五:wu 物:wu
            西:xi 息:xi 希:xi 习:xi 洗:xi 系:xi 下:xia 夏:xia 先:xian 显:xian
            现:xian 线:xian 乡:xiang 相:xiang 想:xiang 向:xiang 小:xiao 校:xiao
            笑:xiao 些:xie 写:xie 谢:xie 心:xin 新:xin 信:xin 星:xing 行:xing
            形:xing 兴:xing 幸:xing 性:xing 姓:xing 休:xiu 修:xiu 需:xu 许:xu
            续:xu 选:xuan 玄:xuan 学:xue 雪:xue 寻:xun 循:xun
            压:ya 牙:ya 言:yan 颜:yan 眼:yan 演:yan 验:yan 阳:yang 养:yang 样:yang
            要:yao 也:ye 业:ye 叶:ye 一:yi 衣:yi 医:yi 已:yi 以:yi 义:yi 易:yi
            意:yi 因:yin 音:yin 阴:yin 银:yin 应:ying 英:ying 影:ying 硬:ying
            用:yong 优:you 由:you 邮:you 游:you 友:you 有:you 又:you 右:you
            于:yu 鱼:yu 愉:yu 雨:yu 语:yu 元:yuan 员:yuan 原:yuan 远:yuan 院:yuan
            愿:yuan 月:yue 越:yue 云:yun 运:yun
            再:zai 在:zai 咱:zan 脏:zang 早:zao 造:zao 则:ze 怎:zen 曾:ceng 增:zeng
            展:zhan 占:zhan 战:zhan 张:zhang 掌:zhang 找:zhao 照:zhao 这:zhe
            真:zhen 争:zheng 整:zheng 正:zheng 证:zheng 之:zhi 支:zhi 知:zhi
            直:zhi 值:zhi 职:zhi 止:zhi 只:zhi 纸:zhi 志:zhi 至:zhi 制:zhi 治:zhi
            中:zhong 终:zhong 钟:zhong 种:zhong 重:zhong 周:zhou 洲:zhou 主:zhu
            住:zhu 助:zhu 注:zhu 专:zhuan 转:zhuan 装:zhuang 准:zhun 着:zhe
            子:zi 自:zi 字:zi 总:zong 走:zou 足:zu 组:zu 最:zui 昨:zuo 左:zuo
            作:zuo 坐:zuo 做:zuo 脚:jiao 序:xu 项:xiang 启:qi 编:bian 辑:ji
            断:duan 清:qing 保:bao 存:cun 程:cheng 系:xi 统:tong 务:wu 键:jian
            鼠:shu 剪:jian 贴:tie 板:ban 享:xiang 全:quan 志:zhi 帮:bang 助:zhu
            关:guan 于:yu 级:ji 础:chu 除:chu 运:yun 试:shi
            """;

        foreach (var token in exact.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split(':');
            if (parts.Length != 2 || parts[0].Length == 0) continue;
            d[parts[0][0]] = parts[1];
        }

        return d;
    }
}
