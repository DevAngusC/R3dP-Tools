# R3dP-Tools Rhino Plugin 專案

這個資料夾是 Rhino 8 外掛 R3dP-Tools 的工作副本。

## PrintGear 尺寸模式

- **齒數與模數**：直接指定齒數與模數。
- **目標含齒外徑**：固定齒數，依目標外徑重新計算模數。
- **目標外徑（保持齒尺寸）**：沿用目前模數，依目標外徑自動選擇最接近的整數齒數；因此齒形尺寸不會隨外徑一起縮放。若要改變齒尺寸，可調整「齒尺寸模數」、壓力角、背隙或齒側間隙。

保持齒尺寸模式會在 Rhino 命令列回報目標外徑與實際可生成外徑的差異。

## 建置

```powershell
dotnet build .\PrintGear\PrintGear.csproj -c Release
```

輸出外掛檔案為 `PrintGear\bin\Release\net8.0-windows\Rhino3DPrintSupport.rhp`。
