// 软件说明知识库：给普通用户看的「这是干什么的、能不能搬、能不能卸、是不是系统软件」。
// 说明文案按社区/日常用法写，不用官方套话；匹配靠关键字命中，最长关键字优先。
using System;
using System.Collections.Generic;
using System.Text;

namespace YijianNuodong.Core
{
    /// <summary>是否属于系统自带 / 运行库 / 普通第三方软件。</summary>
    public enum SoftwareKind
    {
        Unknown = 0,
        ThirdParty = 1,
        System = 2,
        Runtime = 3,
        Driver = 4,
        StoreApp = 5
    }

    /// <summary>搬移建议。</summary>
    public enum MoveAdvice
    {
        Unknown = 0,
        Safe = 1,
        Caution = 2,
        KeepInPlace = 3
    }

    /// <summary>卸载建议。</summary>
    public enum UninstallAdvice
    {
        Unknown = 0,
        Safe = 1,
        Caution = 2,
        DoNotRemove = 3
    }

    /// <summary>一条软件说明。</summary>
    public class SoftwareInfo
    {
        public string MatchedName = string.Empty;
        public string Category = string.Empty;
        public string Summary = string.Empty;
        public string Tip = string.Empty;
        public SoftwareKind Kind = SoftwareKind.Unknown;
        public MoveAdvice Move = MoveAdvice.Unknown;
        public UninstallAdvice Uninstall = UninstallAdvice.Unknown;
        public bool FromCatalog;

        /// <summary>给人看的一句话定性，例如「系统自带组件」「第三方软件」。</summary>
        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case SoftwareKind.System: return "系统自带";
                    case SoftwareKind.Runtime: return "运行库";
                    case SoftwareKind.Driver: return "驱动";
                    case SoftwareKind.StoreApp: return "商店应用";
                    case SoftwareKind.ThirdParty: return "第三方软件";
                    default: return "未收录";
                }
            }
        }

        public bool IsSystemComponent
        {
            get
            {
                return Kind == SoftwareKind.System || Kind == SoftwareKind.Runtime ||
                       Kind == SoftwareKind.Driver || Kind == SoftwareKind.StoreApp;
            }
        }
    }

    /// <summary>
    /// 本地软件说明库。完全离线，不联网、不查官方文档，只给普通用户能看懂的白话说明。
    /// 数据格式（每行一条，用 | 分隔）：
    /// 关键字,关键字,.. | 规范名 | 分类 | 白话说明 | 类型 | 搬移建议 | 卸载建议 | 提示
    /// 类型：N 普通 / S 系统 / R 运行库 / D 驱动 / W 商店应用
    /// 搬移：Y 可以 / C 谨慎 / N 不要
    /// 卸载：Y 可以 / C 注意 / N 不要
    /// </summary>
    public static class SoftwareCatalog
    {
        private const string Data = @"
# ===== 系统自带 / 运行库 / 驱动：默认不建议动 =====
microsoft windows,windows系统,windows组件,系统组件|Windows 系统组件|系统|操作系统自带的东西，删了或搬了系统就起不来。|S|N|N|绝对不要动。
windowsdefender,安全中心,defender|Windows 安全中心|系统|系统自带的杀毒和防火墙，不装第三方杀毒时靠它顶着。|S|N|N|不要卸载。
microsoftedge,microsoft edge|Microsoft Edge 浏览器|系统|系统自带的浏览器，基于 Chromium，其实很好用，很多人把它当主力。|S|N|N|系统组件，建议留着；想换浏览器装别的就行，别删它。
windowsapps,商店应用,microsoftstore|微软商店应用|系统|从微软商店装的应用（照片、微信 UWP 等），装在系统保护的目录里。|W|N|N|商店应用不能手工搬移；要省 C 盘，请在系统设置里改新应用的安装位置。
visualc++,vcredist,vc运行库,vc_redist|Microsoft Visual C++ 运行库|运行库|微软的运行库，一堆软件和游戏靠它才能启动，自己没有任何界面。|R|N|N|千万别删：删了会到处报找不到 dll、程序打不开。
.netframework,.net framework,.net runtime,dotnet,.net|.NET Framework / .NET 运行时|运行库|运行用 .NET 写的程序的基础组件，很多国产软件和游戏依赖它。|R|N|N|不要卸载，卸载会让一批软件直接崩溃。
directx,directx运行时|DirectX|运行库|图形和声音的底层接口，游戏运行必需。|R|N|N|不要动。
nvidia,英伟达,geforce|NVIDIA 显卡驱动|驱动|显卡驱动和设置面板，玩游戏、跑 AI 都靠它。|D|N|C|驱动装在系统目录，搬移没意义还可能黑屏；要升级请用官方安装包。
intel,英特尔|Intel 驱动与组件|驱动|Intel 的芯片组、显卡、蓝牙等驱动。|D|N|N|不要动。
realtek|Realtek 声卡/网卡驱动|驱动|主板集成的声卡、网卡驱动。|D|N|N|不要动。
微软拼音,microsoftime,微软输入法|微软拼音输入法|系统|Windows 自带的中文输入法，切到它就靠它打字。|S|N|N|系统自带，不要卸载也不要搬移。
windows更新,windowsupdate|Windows 更新|系统|系统打补丁、升级版本用的组件。|S|N|N|不要动。
hyper-v,虚拟机平台|Hyper-V / 虚拟机平台|系统|系统自带的虚拟机功能，装 Docker、模拟器时会用到。|S|N|N|不用可以关功能，别手工删目录。

# ===== 安全 / 驱动工具：不建议搬，卸载要谨慎 =====
火绒,huorong|火绒安全|安全软件|国产轻量杀毒，弹窗少，很多人拿它替代 360。|N|C|C|安全软件会挂进系统内核，搬走后防护可能失效，建议卸载后重装到目标盘。
360安全卫士,360safe,360卫士|360 安全卫士|安全软件|清理垃圾、杀毒、装软件一条龙，功能全但弹窗和捆绑较多。|N|C|C|安全类软件不建议搬移；不想要就直接卸载。
腾讯电脑管家,qqpcmgr|腾讯电脑管家|安全软件|腾讯的系统清理和杀毒工具。|N|C|C|同上，不建议搬移。
金山毒霸,duba,kingsoft antivirus|金山毒霸|安全软件|老牌杀毒软件，装机量曾经很大。|N|C|C|同上，不建议搬移。
驱动精灵,drivergenius|驱动精灵|驱动工具|自动找驱动、装驱动，容易捆绑别的软件。|N|C|C|驱动类工具留在系统盘更稳，搬走后可能找不到驱动库。
驱动总裁,drivetaobao|驱动总裁|驱动工具|装机时批量装驱动的工具。|N|C|C|同上。
鲁大师,ludashi|鲁大师|硬件检测|看配置、跑分、温度监控，界面上广告不少。|N|Y|Y|
ccleaner|CCleaner|系统清理|清临时文件、清注册表的小工具，老牌但别乱清注册表。|N|Y|Y|

# ===== 即时通讯 / 办公协作 =====
微信,wechat,weixin|微信|即时通讯|国民级聊天软件，发消息、传文件、扫码支付、看公众号都靠它。程序可以整体搬到别的盘，聊天记录不受影响。|N|Y|Y|搬之前先退出微信，否则会提示文件被占用。
微信输入法,wechatinput|微信输入法|输入法|微信出的中文输入法，语音转文字好用，装上要自己切换才会有。|N|Y|Y|
qq,tim,tencentqq|QQ / TIM|即时通讯|老牌聊天软件，传大文件、群聊、远程协助很常用；TIM 是它的办公版。|N|Y|Y|
钉钉,dingtalk|钉钉|办公协作|公司打卡、审批、开视频会用的办公软件，很多单位强制装。|N|Y|C|单位有统一管理策略时，搬走后可能登不上，先问下管理员。
企业微信,wecom,workweixin|企业微信|办公协作|企业内部聊天和审批工具，和微信打通，拉客户群常用。|N|Y|C|同上，涉及单位策略时先确认。
飞书,feishu,lark|飞书|办公协作|字节的办公套件，文档、表格、视频会都在一个窗口里。|N|Y|Y|
腾讯会议,tencentmeeting,wemeet|腾讯会议|视频会议|开线上会议用的，上课、面试、远程开会都常见。|N|Y|Y|
zoom|Zoom|视频会议|国外常用的视频会议软件，跨国会议常见。|N|Y|Y|
石墨,shimo|石墨文档|办公协作|在线文档和表格，多人同时改同一份。|N|Y|Y|

# ===== 办公套件 / 文档 =====
office,microsoft365,microsoft office|Microsoft Office|办公套件|Word、Excel、PowerPoint 三件套，写文档做表格必备。|N|Y|C|Office 各组件会互相调用，搬完后个别组件可能要「修复安装」。
wps,wps office,kingsoft office|WPS Office|办公套件|国产办公套件，免费、能开 Office 格式，国内单位常用。|N|Y|Y|
福昕,foxit,福昕pdf|福昕 PDF|文档阅读|看和编辑 PDF 的国产软件。|N|Y|Y|
adobe reader,acrobat,adobe acrobat|Adobe Acrobat Reader|文档阅读|看 PDF 的官方阅读器，体积大但兼容性最好。|N|Y|Y|
xmind|Xmind|思维导图|画思维导图、整理思路、做汇报大纲。|N|Y|Y|
亿图,edraw|亿图图示|办公套件|画流程图、思维导图、组织架构图。|N|Y|Y|
visio|Visio|办公套件|画流程图、架构图，Office 家族成员。|N|Y|C|
印象笔记,evernote|印象笔记|笔记记录|老牌笔记软件，剪藏网页、多端同步。|N|Y|Y|
typora|Typora|笔记记录|写 Markdown 的编辑器，所见即所得。|N|Y|Y|
有道词典,youdao|有道词典|学习工具|查单词、翻译整句，学英语常用。|N|Y|Y|
网易邮箱大师,foxmail|邮箱客户端|邮件|在电脑上收发多个邮箱，不用开浏览器。|N|Y|Y|

# ===== 浏览器 / 下载 / 压缩 =====
chrome,googlechrome,google chrome|Google Chrome|浏览器|全球用得最多的浏览器，插件生态最全。|N|Y|Y|
firefox|Firefox|浏览器|开源浏览器，隐私保护做得好。|N|Y|Y|
360浏览器,360se,360chrome,360安全浏览器|360 安全浏览器|浏览器|国内老牌浏览器，兼容各种网银和政务网站。|N|Y|Y|
qq浏览器,qqbrowser|QQ 浏览器|浏览器|腾讯的浏览器，和微信小程序有点联动。|N|Y|Y|
搜狗浏览器,sogouexplorer|搜狗浏览器|浏览器|搜狗出的浏览器。|N|Y|Y|
百度网盘,baidunetdisk|百度网盘|网盘同步|存文件、下资源，国内分享资源最常用的网盘。|N|Y|Y|下载缓存很占空间，也可以只把缓存目录改到别的盘。
阿里云盘,aliyundrive|阿里云盘|网盘同步|不限速的网盘，看影视资源常用。|N|Y|Y|
迅雷,thunder|迅雷|下载工具|下 BT、磁力和大文件的老牌下载器。|N|Y|Y|
idm,internetdownloadmanager|IDM|下载工具|多线程下载器，抓网页视频很好用。|N|Y|Y|
7-zip,7zip|7-Zip|压缩解压|免费开源的压缩解压工具，体积小、没广告。|N|Y|Y|
winrar|WinRAR|压缩解压|老牌压缩软件，能解 RAR 格式。|N|Y|Y|
bandizip|Bandizip|压缩解压|韩国压缩软件，界面清爽。|N|Y|Y|
360压缩,360zip|360 压缩|压缩解压|360 出的压缩软件，带广告。|N|Y|Y|

# ===== 影音 / 图像 / 剪辑 =====
网易云音乐,cloudmusic,netease cloud music|网易云音乐|影音娱乐|听歌软件，歌单和评论区是它的招牌；缓存歌曲很占地方。|N|Y|Y|缓存能占好几 G，搬到别的盘能明显腾出 C 盘空间。
qq音乐,qqmusic|QQ 音乐|影音娱乐|腾讯的听歌软件，曲库大，会员能下无损。|N|Y|Y|
酷狗,kugou|酷狗音乐|影音娱乐|老牌听歌软件，K 歌功能用的人多。|N|Y|Y|
哔哩哔哩,bilibili,b站|哔哩哔哩|影音娱乐|B 站客户端，看视频、追番、看直播。|N|Y|Y|
抖音,douyin|抖音|影音娱乐|短视频客户端，也有直播和商城。|N|Y|Y|
爱奇艺,iqiyi|爱奇艺|影音娱乐|长视频网站客户端，追剧看电影。|N|Y|Y|
优酷,youku|优酷|影音娱乐|阿里系的长视频客户端。|N|Y|Y|
腾讯视频,tencentvideo|腾讯视频|影音娱乐|腾讯的长视频客户端，追剧常用。|N|Y|Y|
potplayer,vlc,mpc-hc|PotPlayer / VLC|影音播放|本地视频播放器，几乎什么格式都能放。|N|Y|Y|
剪映,capcut,jianying|剪映|影音剪辑|抖音出的剪辑软件，剪短视频、加字幕、直接导出，普通人上手最快。|N|Y|Y|草稿和素材默认在用户目录，卸载前先备份工程。
格式工厂,formatfactory|格式工厂|影音转换|视频、音频、图片互相转格式的老工具。|N|Y|Y|
photoshop,adobe photoshop,adobe ps|Photoshop|图像设计|修图、做海报的行业标准软件。|N|Y|C|正版要登录 Adobe 账号，搬完一般能正常用。
illustrator,adobe illustrator|Illustrator|图像设计|画矢量图、做 logo 和排版，和 PS 同一家。|N|Y|C|
premiere,adobe premiere|Premiere|影音剪辑|专业视频剪辑软件，做长片和多轨剪辑用。|N|Y|C|
after effects,aftereffects|After Effects|影音特效|做动效、片头、合成特效，比剪映专业也难学。|N|Y|C|
美图秀秀,meitu|美图秀秀|图像设计|一键美颜、拼图、做证件照，普通人最顺手的修图工具。|N|Y|Y|
autocad,auto cad,acad|AutoCAD|工程制图|画施工图、机械图的行业软件。|N|Y|C|

# ===== 游戏 / 模拟器 =====
steam|Steam|游戏平台|买游戏、装游戏、和好友联机的平台，游戏本体动辄几十上百 G。|N|Y|Y|游戏建议用 Steam 自带的库迁移功能，比手工搬更稳。
epic,epicgames,epic games|Epic Games 启动器|游戏平台|每周领免费游戏、玩堡垒之夜用的平台。|N|Y|Y|
wegame,腾讯游戏平台|WeGame|游戏平台|腾讯的游戏平台，LOL、CF 从这里启动。|N|Y|Y|
uu加速器,网易uu,uu booster|网易 UU 加速器|网络加速|给外服游戏加速、降低延迟。|N|Y|Y|
英雄联盟,leagueoflegends,lol|英雄联盟|游戏|5v5 对战网游，客户端加游戏本体能占几十 G。|N|Y|Y|
原神,genshin impact,genshin|原神|游戏|开放世界游戏，客户端体积很大。|N|Y|Y|
雷电模拟器,ldplayer|雷电模拟器|安卓模拟器|在电脑上跑安卓 App 和手游，装完好几个 G。|N|Y|Y|
夜神模拟器,nox,noxplayer|夜神模拟器|安卓模拟器|安卓模拟器，玩手游用。|N|Y|Y|
mumu,网易mumu|网易 MuMu 模拟器|安卓模拟器|网易的安卓模拟器。|N|Y|Y|

# ===== 开发工具 =====
visualstudiocode,vscode,vs code|Visual Studio Code|开发工具|写代码的编辑器，插件多、启动快，前端和脚本开发最常用。|N|Y|Y|
visualstudio,visual studio,devenv|Visual Studio|开发工具|微软的重量级开发环境，写 C#、C++ 桌面程序用，装全组件能超 20 G。|N|Y|C|体积大，搬之前先关掉所有 VS 窗口。
microsoftvisualstudioinstaller,visual studio installer|Visual Studio Installer|开发工具|微软开发工具的安装器，用来装/改 VS 组件。|N|C|C|它和 VS 绑在一起，单独搬容易出问题。
idea,intellij,pycharm,webstorm,jetbrains,goland,clion|JetBrains IDE|开发工具|IntelliJ IDEA、PyCharm 等写 Java、Python 的 IDE。|N|Y|Y|
python|Python|开发工具|编程语言的运行环境，很多脚本和小工具依赖它。|N|Y|C|有些软件自带 Python，删掉系统的可能影响别的工具。
nodejs,node.js,node js|Node.js|开发工具|JavaScript 运行环境，前端项目构建要用。|N|Y|Y|
git,git for windows|Git|开发工具|版本管理工具，从 GitHub 拉代码要用。|N|Y|Y|
jdk,java,jre,openjdk|Java (JDK / JRE)|开发工具|Java 运行环境，很多企业软件和 Minecraft 依赖它。|N|Y|C|
docker,docker desktop|Docker Desktop|开发工具|打包和运行容器，占空间大、依赖虚拟化。|N|C|C|它依赖系统服务和 WSL，手工搬容易坏；要省空间请用官方设置迁移镜像目录。
xshell,xftp,netsarang|Xshell / Xftp|开发工具|连服务器、传文件的 SSH 工具。|N|Y|Y|
navicat|Navicat|数据库工具|图形化管理 MySQL 等数据库。|N|Y|Y|
postman|Postman|开发工具|调接口、测 API 的工具。|N|Y|Y|
mysql,mysql server|MySQL|数据库|开源数据库，装成服务后跟着系统启动。|N|C|C|它是 Windows 服务，搬完可能连不上，建议先备份数据再考虑重装。
sqlserver,sql server,mssql|SQL Server|数据库|微软的数据库，企业系统常用，服务化程度高。|N|C|C|装成服务并写入系统目录，不建议手工搬移。
unity,unity hub|Unity|开发工具|做游戏和 3D 内容的引擎，编辑器加缓存很容易超 20 G。|N|Y|Y|
notepad++,notepadpp|Notepad++|文本编辑|轻量文本编辑器，改配置、看代码方便。|N|Y|Y|
sublime,sublime text|Sublime Text|文本编辑|启动极快的编辑器。|N|Y|Y|

# ===== 远程控制 / 其他常用 =====
teamviewer|TeamViewer|远程控制|远程帮人修电脑的工具，国外用得多。|N|Y|Y|
向日葵,sunlogin,oray|向日葵远程控制|远程控制|国产远程桌面，速度快。|N|Y|C|它装了系统服务，搬完可能连不上，建议卸载后重装到目标盘。
todesk|ToDesk|远程控制|国产远程控制，免费额度够日常用。|N|Y|C|同上，带系统服务。
anydesk|AnyDesk|远程控制|轻量远程控制软件。|N|Y|Y|
搜狗输入法,sogouime,sogou pinyin|搜狗输入法|输入法|国内用户最多的第三方输入法。|N|Y|Y|卸载前把系统默认输入法切回微软拼音，否则可能打不出字。
百度输入法,baiduime|百度输入法|输入法|百度出的中文输入法。|N|Y|Y|
千牛,qianniu|千牛|电商工具|淘宝天猫卖家的客服和订单工作台。|N|Y|C|带较多消息组件，搬完先试能不能正常收发消息。
";

        private sealed class Entry
        {
            public string[] Keywords;
            public SoftwareInfo Info;
        }

        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly object SyncRoot = new object();
        private static bool _loaded;

        public static int Count
        {
            get
            {
                EnsureLoaded();
                return Entries.Count;
            }
        }

        /// <summary>给一台机器上的软件生成说明：先查库，查不到就按安装位置与名字做常识判断。</summary>
        public static SoftwareInfo Lookup(InstalledProgram program)
        {
            if (program == null) return Guess(string.Empty, string.Empty, string.Empty);
            return Lookup(program.DisplayName, program.Publisher, program.InstallPath);
        }

        public static SoftwareInfo Lookup(string displayName, string publisher, string installPath)
        {
            EnsureLoaded();
            string name = Normalize(displayName);
            string publisherText = Normalize(publisher);
            string leaf = Normalize(LeafOf(installPath));
            string fullPath = Normalize(installPath);

            Entry best = null;
            int bestScore = 0;

            for (int i = 0; i < Entries.Count; i++)
            {
                Entry entry = Entries[i];
                for (int k = 0; k < entry.Keywords.Length; k++)
                {
                    string keyword = entry.Keywords[k];
                    if (keyword.Length == 0) continue;

                    // 短的英文关键字（<=5 字符）只认「开头命中」，否则 digital 里的 git、email 里的 ai
                    // 这类巧合会给出完全错误的说明；中文关键字与长英文词仍按包含匹配。
                    bool strict = IsAscii(keyword) && keyword.Length <= 5;

                    int score = 0;
                    if (name.Length > 0 && Hits(name, keyword, strict))
                    {
                        // 完全相等最强，其次是名字里命中，关键字越长越可信
                        score = (name == keyword ? 400 : 300) + keyword.Length;
                    }
                    else if (leaf.Length > 0 && Hits(leaf, keyword, strict))
                    {
                        score = 200 + keyword.Length;
                    }
                    else if (publisherText.Length > 0 &&
                             (publisherText == keyword ||
                              (!strict && publisherText.IndexOf(keyword, StringComparison.Ordinal) >= 0)))
                    {
                        // 发布者完全相等是很强的信号（Intel -> Intel 驱动与组件），
                        // 包含匹配只对长关键字开放，避免 microsoft 把一切都归到微软。
                        score = (publisherText == keyword ? 250 : 120) + keyword.Length;
                    }
                    else if (fullPath.Length > 0 && !strict && keyword.Length >= 4 && fullPath.IndexOf(keyword, StringComparison.Ordinal) >= 0)
                    {
                        score = 60 + keyword.Length;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = entry;
                    }
                }
            }

            if (best != null)
            {
                SoftwareInfo hit = Clone(best.Info);
                hit.FromCatalog = true;
                hit.MatchedName = string.IsNullOrEmpty(displayName) ? hit.MatchedName : displayName;
                return hit;
            }

            return Guess(displayName, publisher, installPath);
        }

        /// <summary>库里没有时的兜底：按安装位置判断是不是系统东西，其余一律按第三方软件给保守建议。</summary>
        public static SoftwareInfo Guess(string displayName, string publisher, string installPath)
        {
            SoftwareInfo info = new SoftwareInfo();
            info.MatchedName = displayName ?? string.Empty;
            info.Category = "未收录";
            info.Kind = SoftwareKind.Unknown;
            info.FromCatalog = false;

            string path = installPath ?? string.Empty;
            string publisherText = (publisher ?? string.Empty).ToLowerInvariant();
            string nameText = (displayName ?? string.Empty).ToLowerInvariant();

            if (ContainsAny(nameText, "driver package", "驱动包", "驱动安装"))
            {
                info.Kind = SoftwareKind.Driver;
                info.Category = "驱动";
                info.Summary = "这是硬件驱动的安装包（显卡、网卡、蓝牙之类），系统更新时装上的，平时没有界面。";
                info.Tip = "不要卸载也不要搬移：驱动只要装好就不用管它，动了可能让硬件失效。";
                info.Move = MoveAdvice.KeepInPlace;
                info.Uninstall = UninstallAdvice.DoNotRemove;
                return info;
            }

            if (IsUnderWindows(path) || path.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                info.Kind = SoftwareKind.System;
                info.Category = "系统目录";
                info.Summary = "装在 Windows 系统目录里，属于系统自带或系统级组件。";
                info.Tip = "这类东西不要卸载也不要搬移，很容易把系统弄坏。";
                info.Move = MoveAdvice.KeepInPlace;
                info.Uninstall = UninstallAdvice.DoNotRemove;
                return info;
            }

            if (ContainsAny(nameText, "runtime", "运行库", "redistributable", "framework", "sdk", "driver", "驱动", "service pack"))
            {
                info.Kind = SoftwareKind.Runtime;
                info.Category = "运行库";
                info.Summary = "这是一套运行库/开发组件，本身没有可点开的界面，是给别的软件打地基用的。";
                info.Tip = "删掉它通常不会立刻出事，但某些软件下次可能打不开，建议留着。";
                info.Move = MoveAdvice.KeepInPlace;
                info.Uninstall = UninstallAdvice.DoNotRemove;
                return info;
            }

            info.Kind = SoftwareKind.ThirdParty;
            info.Category = "第三方软件";
            if (path.Length == 0)
            {
                info.Summary = "没有从资料库找到它的说明，也没找到安装目录，可能是绿色版或便携版。";
                info.Tip = "不确定用途就先用它的名字在网上搜一下，再决定搬还是卸。";
                info.Move = MoveAdvice.Caution;
                info.Uninstall = UninstallAdvice.Caution;
                return info;
            }

            info.Summary = "没有收录它的专门说明，但从安装位置看是个普通第三方软件（" + ShortPath(path) + "）。";
            info.Tip = ContainsAny(publisherText, "microsoft", "微软")
                ? "发布者写着微软，可能是系统组件，搬之前先确认它是不是驱动或运行库。"
                : "普通软件通常可以整体搬走；不想要就直接卸载，注意先退出程序。";
            info.Move = MoveAdvice.Caution;
            info.Uninstall = UninstallAdvice.Caution;
            return info;
        }

        private static string ShortPath(string path)
        {
            try
            {
                return PathUtil.Shorten(path, 40);
            }
            catch
            {
                return path;
            }
        }

        private static bool ContainsAny(string text, params string[] parts)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < parts.Length; i++)
            {
                if (text.IndexOf(parts[i], StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static bool IsUnderWindows(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (string.IsNullOrEmpty(windir)) return false;
                return PathUtil.IsSubPathOf(path, windir) || PathUtil.SamePath(path, windir);
            }
            catch
            {
                return false;
            }
        }

        private static string LeafOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try
            {
                string trimmed = path.TrimEnd('\\', '/');
                int idx = trimmed.LastIndexOfAny(new char[] { '\\', '/' });
                return idx >= 0 ? trimmed.Substring(idx + 1) : trimmed;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>归一化：去掉空格、括号、连字符等，全小写，方便关键字直接比对。</summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) continue;
                if (c == '(' || c == ')' || c == '[' || c == ']' || c == '{' || c == '}' ||
                    c == '-' || c == '_' || c == '.' || c == ',' || c == '，' || c == '（' || c == '）' ||
                    c == '《' || c == '》' || c == '、' || c == '·' || c == '+' || c == '&' || c == '\'' || c == '"')
                {
                    continue;
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static SoftwareInfo Clone(SoftwareInfo source)
        {
            SoftwareInfo copy = new SoftwareInfo();
            copy.MatchedName = source.MatchedName;
            copy.Category = source.Category;
            copy.Summary = source.Summary;
            copy.Tip = source.Tip;
            copy.Kind = source.Kind;
            copy.Move = source.Move;
            copy.Uninstall = source.Uninstall;
            copy.FromCatalog = source.FromCatalog;
            return copy;
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (SyncRoot)
            {
                if (_loaded) return;
                string[] lines = Data.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;
                    if (line[0] == '#') continue;

                    string[] parts = line.Split('|');
                    if (parts.Length < 8) continue;

                    Entry entry = new Entry();
                    entry.Keywords = SplitKeywords(parts[0]);
                    if (entry.Keywords.Length == 0) continue;

                    SoftwareInfo info = new SoftwareInfo();
                    info.MatchedName = parts[1].Trim();
                    info.Category = parts[2].Trim();
                    info.Summary = parts[3].Trim();
                    info.Kind = ParseKind(parts[4]);
                    info.Move = ParseMove(parts[5]);
                    info.Uninstall = ParseUninstall(parts[6]);
                    info.Tip = parts[7].Trim();
                    info.FromCatalog = true;
                    entry.Info = info;
                    Entries.Add(entry);
                }
                _loaded = true;
            }
        }

        private static string[] SplitKeywords(string raw)
        {
            List<string> list = new List<string>();
            string[] parts = raw.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string keyword = Normalize(parts[i]);
                if (keyword.Length == 0) continue;
                // 拉丁字母只有 2 个字符的关键字太容易误伤（ps / ai / pr 会命中一堆无关软件），
                // 除 qq 这种没有歧义的以外一律忽略；中文两字词（微信、钉钉…）保持可用。
                if (keyword.Length < 3 && IsAscii(keyword) && keyword != "qq") continue;
                if (!list.Contains(keyword)) list.Add(keyword);
            }
            return list.ToArray();
        }

        /// <summary>strict=true 时只接受「整词相等」或「从开头命中」。</summary>
        private static bool Hits(string text, string keyword, bool strict)
        {
            if (text.Length == 0 || keyword.Length == 0) return false;
            if (text == keyword) return true;
            if (!strict) return text.IndexOf(keyword, StringComparison.Ordinal) >= 0;
            return text.StartsWith(keyword, StringComparison.Ordinal);
        }

        private static bool IsAscii(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] > 127) return false;
            }
            return true;
        }

        private static SoftwareKind ParseKind(string raw)
        {
            switch ((raw ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "S": return SoftwareKind.System;
                case "R": return SoftwareKind.Runtime;
                case "D": return SoftwareKind.Driver;
                case "W": return SoftwareKind.StoreApp;
                case "N": return SoftwareKind.ThirdParty;
                default: return SoftwareKind.Unknown;
            }
        }

        private static MoveAdvice ParseMove(string raw)
        {
            switch ((raw ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "Y": return MoveAdvice.Safe;
                case "C": return MoveAdvice.Caution;
                case "N": return MoveAdvice.KeepInPlace;
                default: return MoveAdvice.Unknown;
            }
        }

        private static UninstallAdvice ParseUninstall(string raw)
        {
            switch ((raw ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "Y": return UninstallAdvice.Safe;
                case "C": return UninstallAdvice.Caution;
                case "N": return UninstallAdvice.DoNotRemove;
                default: return UninstallAdvice.Unknown;
            }
        }
    }
}
