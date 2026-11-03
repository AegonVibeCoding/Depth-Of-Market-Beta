# Depth Of Market Beta 1.0

ATAS 自定义盘口指标，指标列表中的名称为 **depth of market beta**。

提供原生盘口、Net/Stacked/Pulled/Both 四个独立挂撤单统计栏，以及可选的黄线密集区观察功能。

## 下载

- [完整指标包：ATAS、ATAS X 和使用说明](Indicators/Depth-Of-Market-1.0.zip)
- [普通 ATAS DLL](Indicators/ATAS/DepthOfMarketBeta.dll)
- [ATAS X DLL](Indicators/ATASX/DepthOfMarketBeta.dll)

在 GitHub 文件页面点击下载原始文件，选择对应平台的 DLL。

## 安装

将 DLL 放入对应平台的自定义指标目录。普通 ATAS 通常为 `%APPDATA%\ATAS\Indicators`，ATAS X 使用其自身目录。

重启平台，在指标列表搜索 **depth of market beta** 并添加。使用实时 Level 2 盘口和成交数据；指标无需激活码。黄线功能默认关闭，需要在 Z1 手动开启。

## 默认设置

| 项目 | 默认值 |
|---|---|
| 原生 DOM 宽度 | 100像素 |
| Net距离 | 150像素 |
| Both距离 | 300像素 |
| 仅堆叠、仅拉取 | 关闭 |
| Net/Both增加色 | 青绿 #FF089981 |
| Net/Both减少色 | 红色 #FFF2385A |

距离是各栏右边缘距图表右侧的距离。模板会保留已保存的设置。

## 文档与源码

- [使用说明](docs/使用说明.md)：安装、参数、黄线逻辑和常见问题。
- [代码说明](docs/代码说明.md)：源码结构、计算流程和编译方法。
- [源码](DepthOfMarketBeta/)：C# 项目和编译脚本。
- [指标文件](Indicators/)：两版 DLL、完整 ZIP 和文件校验值。

需要 Windows、.NET 10 SDK 和本机对应平台 SDK 才能编译。仓库不附带平台 SDK。

盘口减少包含撤单与成交消耗；黄线用于观察市场行为，不直接代表买卖入场信号。
