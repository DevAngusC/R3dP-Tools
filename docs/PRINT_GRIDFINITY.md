# PrintGridfinity（實體／鏤空母座）

新增 **Output／生成內容** 選項：`Female／只生成母座`、`Male／只生成公座`、`Pair／公母成對`（首次預設）。選擇會在目前 Rhino 工作階段保留。單件模式只新增一個物件，實際外框左下角置於所選位置；成對模式維持母座在左、公座在右的配置。母座類型仍可選實體或鏤空；只生成公座時，母座類型不改變公座幾何。User Text 記錄 `PrintGridfinity.Output`。

以下並排配置與組裝平移說明適用於「公母成對」模式。

輸入 `PrintGridfinity`，設定 X／Y 格數，按 Enter，再指定母座左下角底部的位置。輸出 World XY 上並排的兩個封閉 Brep：一體式母座方陣（實體磁吸或鏤空輕量），以及同格數、上表面平坦的一體式公座轉接底板。公座位於母座右側，兩件間距 10 mm，底面皆在所選點的 Z 高度。公座不是收納盒，也不是一組分離的 1×1 底腳；可供後續與自訂零件結合。

## 選項（全部以毫米輸入）

| 選項 | 預設 | 範圍／用途 |
| --- | ---: | --- |
| BaseStyle／母座類型 | Solid／實體磁吸母座 | Solid 或 Lightweight／鏤空輕量母座 |
| Columns／X格數 | 2 | 1–20 |
| Rows／Y格數 | 2 | 1–20；總格數最多 100 |
| Clearance／單側間隙mm | 0.25 | 0.10–0.40；調整母座，公座保持標準外形 |
| MagnetDiameterAllowance／磁鐵孔直徑加大量mm | 0.5 | 0–0.8；實際孔徑 = 6 + 此值 |
| MagnetDepthAllowance／磁鐵孔深度加大量mm | 0.4 | 0–0.6；實際孔深 = 2 + 此值 |

可用英文選項進行 Rhino 指令腳本操作。參數在同一次 Rhino 工作階段保留。幾何先以 mm 計算，再自動換算成文件單位；無單位或自訂單位文件會拒絕生成。輸出物件 User Text 記錄尺寸、角色、格數、公差與孔數，數值單位皆為 mm。

## 母座類型

- **實體磁吸母座（Solid，首次預設）**：保留 3.2 mm 底板及每格四個磁鐵盲孔，總高 7.6 mm。
- **鏤空輕量母座（Lightweight）**：如參考照片的格柵，每格上下貫通，沒有底板或母座磁鐵孔，總高 4.4 mm。保留相同格距、圓角及公座配合剖面。格柵本身仍是可列印的封閉 Brep。公座仍保留磁鐵孔，以便搭配其他磁吸母座。
- 輕量版公座與母座底面同高即可裝配，不需上移 3.2 mm。將底板放在平面上使用；鏤空格心不提供支承面。
- 選擇會在目前 Rhino 工作階段記住。User Text 新增 BaseStyle、AssemblyLift；輕量母座孔數、孔徑與孔深記錄為 0。

## 尺寸及配合

- 格距 42 mm。母座外框為 `42X × 42Y mm`；公座頂板為 `(42X−0.5) × (42Y−0.5) mm`，外角 R3.75。
- 公座底腳剖面：Z=0 時寬 35.6、R0.8；Z=0.8 時寬 37.2、R1.6；直壁到 Z=2.6；45° 上斜面到 Z=4.75 時寬 41.5、R3.75。公座總高 7 mm。
- 標準母座剖面：局部 Z=0 時寬 36.3；Z=0.7 時寬 37.7；直壁到 Z=2.5；45° 上斜面延伸至 Z=4.65 時寬 42。預設直壁配合每側 0.25 mm，下導角因原始母座剖面與公座略異而具有額外避讓。圓角與寬度同時依公差調整。
- 實體磁吸版母座以 3.2 mm 實心底板承載磁鐵；母座剖面在 4.4 mm 處截斷，總高 7.6 mm，保留頂部壁厚，避免標準剖面到 42 mm 格界時出現零厚度邊。這是有底磁吸版本，不是原版鏤空底板的厚度。
- 實體磁吸版組裝時公座底面落在母座 Z=3.2 mm 的凹槽底部。並排生成的公座可沿 X 移動 `−(42X+10)` mm、Z 移動 `+3.2` mm 來檢查裝配。

## Ø6 × 2 mm 磁鐵

實體磁吸模式每一格的公座與母座各有四個盲孔，孔中心相對格中心為 `(±13, ±13)` mm，即 26×26 mm 孔距。2×3 方陣每件 24 孔，共 48 孔。公座從底部裝入，母座從凹槽底面裝入；兩側磁鐵朝向彼此。安裝前確認極性。

公開原型工程圖為 Ø6.5×2.4 mm 孔，為 Ø6×2 mm 磁鐵留出裝配／膠黏空間，本版沿用該預設。這不是壓入配合；可使用膠固定，孔深餘量也會影響磁鐵表面距離。需要精確 Ø6×2 mm 孔時，將兩個加大量均設為 0；孔可能因列印收縮而偏緊，應先試印。母座最薄孔底在最大孔深時仍有 0.6 mm。

## 規格來源與限制

- [Gridfinity 社群規格](https://github.com/gridfinity-unofficial/specification)：42 mm 格距、41.5 mm 公座及每格四角 Ø6×2 mm 磁鐵。
- [Stu142 原始模型工程圖](https://stu142.com/Gridfinity-Documentation/)：[公座剖面](https://stu142.com/Gridfinity-Documentation/drawing_svg/bin_bottom_profile.svg)、[母座剖面](https://stu142.com/Gridfinity-Documentation/drawing_svg/baseplate_profile.svg)、[圓角與磁鐵孔](https://stu142.com/Gridfinity-Documentation/drawing_svg/bin_radius.svg)。查閱日期 2026-10-09。

0.25 mm 是標準幾何配合起點，並非任何印表機／材料皆適用的「最佳公差」。建議先印 1×1，校正象腳、流量與孔徑，再產生大方陣。尚未完成實物試印或人工 Rhino 操作驗收。大方陣的布林運算可能耗時；每格之間可用 Esc 取消。若任一步驟失敗，不留下部分結果。未提供螺絲孔、堆疊唇、收納盒壁或分片列印。

## 驗證

鏤空版更新：Release 建置 0 警告／0 錯誤，11 組檢查全部通過（5 組實體、4 組鏤空、無效輸入、取消）。每種母座均驗證封閉實體及封閉列印網格，並提供各自的 2×3 示範檔。

2026-10-09：Release 建置 0 警告／0 錯誤，下述 7 組檢查全部通過。精確就座位置採實體內外取樣，另將公座上抬 0.01 mm 後檢查布林交集體積，避免 Rhino 對共面接觸回傳不確定結果。尚未以實物或第三方模型完成互通驗收。

`tests/GridfinityChecks` 使用獨立隱藏 Rhino 核心，檢查封閉 Brep、外形尺寸、每個磁鐵盲孔的孔徑與孔底、直壁間隙、公母座組合無體積碰撞、封閉列印網格、英吋換算、無效輸入和取消。涵蓋實體版 1×1、2×3、3×1、1×2、4×4，以及鏤空版 1×1、2×3、3×1、1×2 和可調尺寸邊界；鏤空版另檢查全厚度貫通。測試產生 `artifacts/gridfinity-checks/Gridfinity-Solid-2x3.3dm` 與 `Gridfinity-Lightweight-2x3.3dm` 示範檔與 `checks.txt`。

```powershell
dotnet build .\PrintGear\PrintGear.csproj -c Release
dotnet run --project .\tests\GridfinityChecks\GridfinityChecks.csproj -c Release
```

建置產物 `PrintGear/bin/Release/net8.0-windows/Rhino3DPrintSupport.rhp`。2026-10-09 已依使用者要求安裝至本機正式位置，並更新兩個歷史載入副本。獨立 Rhino 開啟舊模型已驗證自動載入新版，且實際執行 PrintGridfinity 1×1 成功產生公母座。安裝證據位於 `artifacts/install-verifier/gridfinity-startup.json`。後續更新仍需核對實際載入路徑。
