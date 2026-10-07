# DESIGN.md — XAssistant 界面规范

> 参考来源：`awesome-design-md` skill 收录的 **Linear** 设计系统
> （`references/linear.app.md`，54 个真实产品规范之一）。
> Linear 被选中的理由是：它是「精确、克制、信息密度高」这一类现代数据工具的
> 代表，与本项目（长期后台运行、数据密集、需要一眼看清统计）的定位一致。
>
> **已做的取舍**：Linear 是 dark-first，而本机是浅色桌面环境，
> 因此采用其 **Light Mode Neutrals** 与排版系统，不使用深色画布。
> 品牌色也不沿用其靛紫（那是 Linear 的品牌资产），换成本项目自有的蓝。

---

## 1. 核心理念

1. **背景层次表达结构，而不是靠线和框。**
   Linear 用「背景亮度阶梯」表达层级，而非堆叠边框。本项目对应：
   页面白 → 侧边栏极浅灰 → 卡片白 + 1px 极浅边框。
2. **边框要轻到几乎看不见。** 用于分隔，不用于装饰。
3. **排版承担主要的信息层级。** 字号/字重/字距三者共同分级，
   而不是靠颜色和加粗硬拉对比。
4. **强调色是稀缺资源。** 只用于可交互元素与关键状态，
   绝不用于装饰。整屏应该只有一两处彩色。
5. **大字号用负字距。** 这是 Linear 最显著的特征之一 ——
   字号越大，字距越紧，形成「被压缩的权威感」。

---

## 2. 颜色令牌

实现见 `Themes/Palette.xaml`。**界面禁止写颜色字面量。**

### 背景与表面

| 令牌 | 值 | 用途 |
|---|---|---|
| `Brush.Background` | `#FFFFFF` | 内容区页面底色 |
| `Brush.Sidebar` | `#F7F8F8` | 侧边栏（Linear 的 Light Background） |
| `Brush.Surface` | `#FFFFFF` | 卡片 |
| `Brush.Surface.Alt` | `#F7F8F8` | 次级表面、输入框底 |
| `Brush.Hover` | `#F1F2F4` | 悬停 |
| `Brush.Pressed` | `#E6E8EB` | 按下 |
| `Brush.Selected` | `#EEF2FF` | 选中（主色极浅版） |

### 边框

| 令牌 | 值 | 用途 |
|---|---|---|
| `Brush.Border` | `#E6E8EB` | 默认边框（Linear Light Border Alt） |
| `Brush.Border.Strong` | `#D0D6E0` | 需要更明确时（Linear Light Border） |
| `Brush.Divider` | `#EDEFF2` | 分隔线，比边框更浅 |

### 文字

| 令牌 | 值 | 用途 |
|---|---|---|
| `Brush.Text.Primary` | `#191B1F` | 正文、数字。**不用纯黑**，减眼疲劳 |
| `Brush.Text.Secondary` | `#5C6169` | 说明、次要信息 |
| `Brush.Text.Tertiary` | `#8A8F98` | 标签、单位、时间戳（Linear Tertiary） |
| `Brush.Text.Disabled` | `#B4B8BF` | 禁用 |
| `Brush.Text.OnAccent` | `#FFFFFF` | 主色底上的文字 |

### 强调与状态

| 令牌 | 值 | 用途 |
|---|---|---|
| `Brush.Accent` | `#2563EB` | 唯一的主色。选中态、主按钮、数据条 |
| `Brush.Accent.Hover` | `#3B76F0` | |
| `Brush.Accent.Pressed` | `#1D4ED8` | |
| `Brush.Accent.Subtle` | `#EEF2FF` | 主色浅底（选中行、标记） |
| `Brush.Success` | `#0E9F6E` | 记录中等正常状态 |
| `Brush.Warning` | `#B45309` | 提示 |
| `Brush.Error` | `#DC2626` | 错误 |

### 数据可视化专用

| 令牌 | 值 | 用途 |
|---|---|---|
| `Brush.Chart.Primary` | `#2563EB` | 当前周期 / 主数据条 |
| `Brush.Chart.Muted` | `#DBE4F7` | 对比周期 / 次要数据条 |
| `Brush.Chart.Track` | `#F1F2F4` | 条形图轨道底 |

---

## 3. 排版

实现见 `Themes/Typography.xaml`。

**字体族**：`Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI`
（Linear 用 Inter Variable；本项目不引入外部字体依赖，
用 Windows 自带的 Segoe UI Variable 达到相近的现代观感，中文回退雅黑。）

**字重**：Linear 的标志性字重是 510（介于 400 与 500 之间）。
WPF 的 `FontWeight` 只支持百位整数，无法表达 510，因此映射为：
400 → `Normal`，510 → `Medium`，590 → `SemiBold`。

| 角色 | 字号 | 字重 | 字距 | 用途 |
|---|---|---|---|---|
| `Text.MetricLarge` | 28 | SemiBold | -0.6 | 页面级大数字 |
| `Text.Metric` | 20 | SemiBold | -0.4 | 卡片内数字 |
| `Text.Title` | 20 | SemiBold | -0.3 | 页面标题 |
| `Text.SectionHeader` | 14 | SemiBold | -0.1 | 卡片/分组标题 |
| `Text.Body` | 13 | Normal | 0 | 正文 |
| `Text.BodyStrong` | 13 | Medium | 0 | 需强调的正文 |
| `Text.Secondary` | 13 | Normal | 0 | 次要说明 |
| `Text.Caption` | 11 | Normal | 0 | 单位、标签、时间 |
| `Text.Mono` | 12 | Normal | 0 | 路径、类名等技术内容 |

**负字距原则**：≥20px 用 `-0.3` 到 `-0.6`；14px 用 `-0.1`；≤13px 归零。
参考 Linear：72px→-1.584px，48px→-1.056px，32px→-0.704px，24px 以下趋近 0。

---

## 4. 圆角与间距

**圆角阶梯**（借用 Linear 的尺度）

| 令牌 | 值 | 用途 |
|---|---|---|
| `Radius.Small` | 4 | 徽标、小容器 |
| `Radius.Medium` | 6 | 按钮、输入框、列表项 |
| `Radius.Large` | 8 | 卡片 |
| `Radius.Panel` | 12 | 大面板 |
| `Radius.Pill` | 999 | 药丸标签、开关 |

**间距**：以 8 为基准 —— 4 / 8 / 12 / 16 / 24 / 32。

---

## 5. 组件

实现见 `Themes/Controls.xaml`。

### 导航项 `Nav.Item`
ListBoxItem，三态：透明 → 悬停 `Brush.Hover` → 选中 `Brush.Selected` + 文字转主色 + Medium 字重。
圆角 6，内边距 12,9。图标用 Segoe Fluent Icons 字形。

### 按钮
- `Button.Primary`：主色底白字，圆角 6，内边距 16,7
- `Button.Secondary`：白底 + `Brush.Border` 1px，圆角 6
- `Button.Ghost`：无底无框，仅悬停时出现浅底（工具栏用）

### 开关 `Switch`
**替代原生 CheckBox。** 设置页必须用它 ——
WPF 默认 CheckBox 是 Windows 95 观感的小方框，是「不现代」的典型来源。
规格：宽 40、高 22、轨道圆角 999；关闭态轨道 `Brush.Border.Strong`、
开启态 `Brush.Accent`；滑块白色圆形 16，位置动画过渡 150ms。

### 卡片 `Card`
`Brush.Surface` + 1px `Brush.Border` + 圆角 8 + 内边距 16。

### 数据表格 `DataGrid.Modern`
无外框、无网格线；表头透明底小号灰字 + 下方 1px 分隔线；行高 38；
悬停浅灰、选中浅蓝；数字列右对齐 + 等宽字体。

### 数据可视化（本项目新增，Linear 规范未覆盖）

| 组件 | 说明 |
|---|---|
| `MetricCard` | 指标卡：大数字 + 单位 + 标签，可选副标题（如「较昨日 +12%」） |
| `BarRow` | 横向排行条：标签 + 进度条 + 数值。用于 Top N 排行 |
| `StackedBar` | 占比条：单条按比例分色。用于按键类型分布 |
| `SparkBar` | 迷你柱状图：一组等宽竖条。用于 24 小时分布、近 7 日趋势 |

**为什么自己做而不引第三方图表库**：本项目的数据形态简单（排行、占比、分布），
三种几何形状即可覆盖；引入 OxyPlot / LiveCharts 会增加依赖体积与升级负担，
而视觉上反而更难与设计系统统一。

---

## 6. 页面结构约定

每个页面统一：

```
页面标题（Text.Title）
  └ 一句话说明（Text.Secondary）
  └ [可选] 顶部指标区 —— MetricCard 横向排列
  └ [可选] 可视化区 —— 图表卡片
  └ 明细区 —— DataGrid.Modern 或列表
```

- 页面内边距 24
- 卡片之间间距 16
- 页面内**最多两处**使用强调色

---

## 7. 禁止事项

- ❌ 界面中出现颜色字面量（一律引用令牌）
- ❌ 使用 WPF 默认 CheckBox（改用 `Switch`）
- ❌ 使用 WPF 默认 DataGrid 外观（改用 `DataGrid.Modern`）
- ❌ 在正文用纯黑 `#000000`
- ❌ 用加粗 + 颜色同时强调（选其一）
- ❌ 装饰性使用主色
- ❌ 大字号用正值字距
