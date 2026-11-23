<p align="center">
  <a href="README.md">English</a> ·
  <a href="README_zh_cn.md">简体中文</a> ·
  <a href="README_zh_tw.md">繁體中文</a> ·
  <a href="README_ja.md">日本語</a> ·
  <a href="README_ko.md">한국어</a> ·
  <a href="README_de.md">Deutsch</a> ·
  <strong>Español</strong> ·
  <a href="README_fr.md">Français</a> ·
  <a href="README_pt_br.md">Português (Brasil)</a> ·
  <a href="README_ru.md">Русский</a>
</p>

<h1 align="center">AMD Ryzen PBO Studio</h1>

<p align="center">
  Herramienta de ajuste PBO, pruebas automáticas de Curve Optimizer (CO) y overclocking de memoria para procesadores AMD Ryzen series 5000 / 7000 / 9000.
</p>

![Ajuste PBO](pbotuning.png)

## Funciones

### Interfaz y monitorización en tiempo real

La barra lateral izquierda cambia entre **Ajuste PBO**, **Prueba de estabilidad**, **OC de memoria** y **Registro de ejecución**; abajo muestra **Resumen del sistema** (procesador, memoria y FCLK/UCLK en vivo, placa base, BIOS y AGESA, controlador gráfico y sistema) y el interruptor **Aplicar PBO al iniciar**.

En la parte superior derecha está el monitor por núcleo, que muestra:

- Valores actuales y límites de PPT, TDC, EDC y THM
- TEL/VID, Vdroop, Fmax y BCLK (`BCLK/BCLK2` con reloj asíncrono)
- Estado de los núcleos por CCD: uso medio y temperatura hotspot (`HOT`) de cada CCD, además del ranking CPPC (marcadores oro / plata), frecuencia, frecuencia efectiva (`EFF`), valor CO y Core VID de cada núcleo

### Ajuste PBO

- **Curve Optimizer (CO)**: offsets por núcleo físico con ajuste `+/-` por columna, además de aplicar, actualizar, guardar y cargar perfiles
- **Curve Shaper (CS)**: 5 bandas de frecuencia (`MIN` / `LOW` / `MED` / `HIGH` / `MAX`) × 3 bandas de temperatura (`LOW` / `MED` / `HIGH`), solo en Ryzen serie 9000 (Zen 5)
- **Parámetros PBO y Scalar**: escribe FMax, PPT, EDC, TDC y PBO Scalar (1X–10X) a la vez
- **Aplicar PBO al iniciar**: al iniciar sesión en Windows escribe automáticamente los CO / CS guardados y los valores guardados/aplicados de FMax, PPT, EDC y TDC. Pase una prueba de estrés antes de activarlo; si el sistema se reinicia de forma anormal tras aplicarlo, el siguiente arranque lo omitirá automáticamente

### Prueba de estabilidad

![Prueba de estabilidad](testing.png)

#### Boost mononúcleo y Alineación de voltaje

Ambas funciones están a la derecha de **Prueba de estabilidad** y requieren CPU con AVX2 / FMA:

- **Boost mononúcleo**: mide frecuencia, frecuencia efectiva, VID y Core VID de cada núcleo con el CO actual sin cambiar ningún parámetro
- **Alineación de voltaje**: toma como referencia el mejor núcleo de cada CCD, ajusta el CO del resto para igualar su voltaje a la misma frecuencia y mide el techo de frecuencia mononúcleo de cada CCD. El resultado solo se rellena en el editor de CO sin guardarse automáticamente; **debe pasar una prueba de estrés antes de usarlo**

#### Prueba de estrés

La prueba de estrés usa **y-cruncher** en tres modos:

| Modo       | Descripción                                                                       |
| ---------- | --------------------------------------------------------------------------------- |
| Individual | Elige una prueba entre VT3, BKT, SVT, BBP, SFTv4, SNT, FFTv4, N63 o YC018         |
| Secuencial | Ejecuta en orden; por defecto `VT3 → BKT → SVT`, 20 / 10 / 10 rondas              |
| Combinado  | Rota las pruebas elegidas en cada ronda; por defecto `VT3`, `BKT`, `SVT`, 10 rondas |

Pruebas, rondas y duración por ronda son personalizables. `YC018` es un benchmark de Pi intensivo en memoria cuyo tamaño se selecciona según la RAM instalada y siempre usa todos los núcleos.

Alcance:

| Alcance           | Descripción                                                       |
| ----------------- | ----------------------------------------------------------------- |
| Todos los núcleos | Estresa todos los núcleos a la vez                                |
| Personalizado     | Estresa solo los núcleos físicos marcados                         |
| Un solo CCD       | Estresa solo el CCD elegido (solo CPUs multi-CCD)                 |
| Cada CCD          | Ejecuta la prueba completa en cada CCD por turno (solo multi-CCD) |

Los resultados de un solo CCD son orientativos; confirme la estabilidad final con todos los núcleos.

Ajuste de CO:

- **Automático**: cuando y-cruncher reporta un error, asigna el núcleo lógico afectado a su núcleo físico, retrocede su CO (por defecto `+2`, ajustable de `+1` a `+5`) y repite la ronda hasta pasar
- **Manual**: avisa y detiene la prueba sin cambiar ningún parámetro

### OC de memoria

> La función de OC de memoria cuenta con el soporte de [Cat-girl-memoryOC](https://v.douyin.com/yMpmAidKqdY/) (`@纯爱出演小猪`).

![OC de memoria](dramoc.png)

- **Info SPD de memoria**: resumen de memoria actual, modelo y capacidad por ranura, fabricante de chips (ICs) y perfiles EXPO leídos del SPD
- **Escritura de memoria y perfiles**: **Escribir en BIOS**, **Revertir**, **Importar**, **Exportar**, con la cantidad de parámetros ajustables cargados y el estado actual debajo
- **Voltajes y varios**: `SOC`, `VDDP`, `VDDG CCD / IOD`, `VDD MISC`, `CPU VDDIO`, `DRAM VDD / VDDQ / VPP`, promedio de `DIMM Temp` (sensor SPD) y `DIMM Power` (PMIC; pase el cursor para ver lecturas individuales), `FCLK OC Mode` y valores de solo lectura `BGS`, `BGS Alt`, `GDM`, `Cmd2T`. Los voltajes SoC se leen de la PM Table y los DRAM del PMIC cada 2 segundos
- **Control de timings DRAM**: `Memory Frequency`, `FCLK`, `UCLK DIV1 MODE`, `PowerDown Enable`, `Refresh Mode` y todos los timings del controlador de memoria
- **Control de señal**: `ProcODT`, drive strengths, `Rtt`, `Nitro`, `Robust Training`, `Interleave`, etc.

#### Relleno EXPO e importación / exportación

- **EXPO**: al pulsar un perfil EXPO en el SPD se rellena el preajuste EXPO correspondiente; al pulsarlo de nuevo se restaura el valor anterior. Hay que pulsar **Escribir en BIOS** para enviarlo.
- **Exportar**: guarda los objetivos actuales en un `.json` dentro de `dram\` (nombrado por plataforma, frecuencia, CL e IC, p. ej. `DESKTOP-DDR5-6400-CL28-Hynix.json`). Cada entrada guarda la función `AMD_ACPI` y `"auto": true` o el `value` bruto de la BIOS (`label` y `display` son solo para lectura):

```json
{
  "format": "RyzenPboStudio.MemoryConfig",
  "version": 1,
  "source": "targets",
  "memoryType": "DDR5",
  "settings": [
    { "function": "Set Mem Clock", "label": "Memory Frequency", "auto": false, "value": 3200, "display": "DDR5-6400" },
    { "function": "Set VDDIO", "label": "DRAM VDD (V)", "auto": false, "value": 1400, "display": "1.400" },
    { "function": "Set TrrdL", "label": "TrrdL", "auto": true, "display": "Auto" }
  ]
}
```

- **Importar**: admite archivos `RyzenPboStudio.MemoryConfig` y configuraciones de **Cat-girl memoryOC** (configuración de memoria o parte de memoria de un perfil combinado). Las entradas `Auto` restauran el valor actual de la BIOS; las opciones no soportadas por la placa se indican en la barra de estado. Pulse **Revertir** para volver a los valores actuales de la BIOS

#### Escribir en BIOS

- Los parámetros se escriben mediante la interfaz WMI `AMD_ACPI` de la placa base. El software no puede escribir valores Auto
- **Escribir en BIOS** está bloqueado por defecto; active primero **Modo experto** en **Ajustes**
- `CPU VDDIO` tiene un límite de `1.500 V` y requiere un objetivo antes de escribir (no puede quedar en Auto)
- La ventana de confirmación marca objetivos claramente anómalos (en el mínimo/máximo de la BIOS o ≥2× / ≤0,5× del valor actual). Antes de escribir comprueba los IDs de firmware y valores clave, respalda los valores actuales de la BIOS en `profiles\memory_backup_<hora>.json` (se puede reimportar), envía cada comando uno por uno (deteniéndose ante cualquier error), vuelve a leer cada elemento y guarda los comandos y respuestas en `logs\acpi-command-trace.jsonl`
- Tras escribir es necesario **reiniciar y completar el entrenamiento de memoria** para que surta efecto. **Parámetros erróneos pueden causar pantalla negra e impedir el arranque**: en escritorio hay que limpiar el CMOS; los portátiles no suelen tener botón externo y pueden requerir abrir el equipo para desconectar la batería principal y la pila CMOS. El programa no puede recuperarse automáticamente

### Registro de ejecución y recuperación tras fallos

![Registro de ejecución](logging.png)

- **Registro de ejecución**: el registro completo se consulta en **Registro de ejecución** (con una ventana sincronizada a la derecha de **Ajuste PBO**), guardando rondas y velocidad de y-cruncher, mapeo de núcleos lógicos a físicos, retrocesos de CO y escrituras en BIOS; cada inicio se guarda en `logs\`
- **Recuperación tras fallos**: tras un bloqueo o corte de energía durante una prueba, el programa arranca automáticamente al reiniciar, restaura los límites PBO y Curve Shaper, retrocede el CO un paso (o mantiene el CO manual si se modificó tras el corte) y reanuda la fase interrumpida; se puede cancelar en cualquier momento. Para pruebas nocturnas desatendidas active el inicio de sesión automático de Windows

### Ajustes e idiomas

Pulse el **icono de engranaje** en la parte inferior izquierda para abrir **Ajustes**:

- **General**: idioma, confirmar antes de salir y **Modo experto** (omite avisos rutinarios de prueba / recuperación / inicio y desbloquea **Escribir en BIOS**)
- **Actualizaciones**: buscar actualizaciones al iniciar
- **Monitor**: intervalo de actualización (`100–10000 ms`, por defecto `500 ms`)
- **Datos**: acceso directo a las carpetas `profiles` y `logs`

El inglés viene integrado y `lang\` incluye 9 traducciones (`zh-Hans`, `zh-Hant`, `ja`, `ko`, `de`, `es`, `fr`, `pt-BR`, `ru`). Por defecto sigue el idioma de Windows y usa inglés si no lo encuentra. Cada `<código>.ini` usa `Texto en inglés=traducción` (manteniendo marcadores como `{0}`); para añadir un idioma basta copiar un archivo, renombrarlo y editar `name=` bajo `[meta]`. El cambio aplica al reiniciar.

## Requisitos

| Elemento    | Requisito                                                                                                |
| ----------- | -------------------------------------------------------------------------------------------------------- |
| Procesador  | AMD Ryzen series 5000 / 7000 / 9000                                                                      |
| Sistema     | Windows 10 / 11 x64                                                                                      |
| Runtime     | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (no necesario con `-net8contained.zip`) |
| Controlador | [PawnIO](https://pawnio.eu) (obligatorio; sin él el programa no arranca)                                 |
| Permisos    | Administrador                                                                                            |

Soporte por generación:

| Función         | Serie 5000 (Zen 3)            | Serie 7000 (Zen 4) | Serie 9000 (Zen 5) |
| --------------- | ----------------------------- | ------------------ | ------------------ |
| Curve Optimizer | Sí                            | Sí                 | Sí                 |
| Curve Shaper    | No                            | No                 | Sí                 |
| PPT / EDC / TDC | Sí                            | Sí                 | Sí                 |
| FMax            | No                            | Sí                 | Sí                 |
| Ranking CPPC    | Requiere CPPC activo en BIOS  | Sí                 | Sí                 |

## Uso

1. Descargue el paquete correspondiente en [Releases](https://github.com/lengmo23/RyzenPboStudio/releases) y extráigalo en cualquier carpeta:
   - **`-lite.zip`**: incluye y-cruncher, requiere .NET 8 Desktop Runtime instalado
   - **`-net8contained.zip`**: incluye .NET 8 Runtime y y-cruncher, funciona sin instalar .NET 8
   - `-update.zip` y `-net8patch-rt*.zip` son solo para actualización automática, no incluyen y-cruncher y no sirven para la primera instalación
2. Instale [PawnIO](https://pawnio.eu) y ejecute `AMD Ryzen PBO Studio.exe` como administrador
3. En **Ajuste PBO**, verifique que las frecuencias por núcleo, CO y lecturas de potencia sean normales
4. Cambie a **Prueba de estabilidad**, elija modo, alcance y ajuste de CO y pulse **Iniciar**
5. Al pasar todas las pruebas, **Registro de ejecución** muestra los CO y CS aprobados y el CO final se guarda también en `profiles\final_offsets.txt`
6. Para ajustar memoria, vaya a **OC de memoria**, edite o importe objetivos (o rellene EXPO), active **Modo experto** en **Ajustes**, pulse **Escribir en BIOS** y reinicie para completar el entrenamiento de memoria

El programa utiliza tres carpetas en el mismo directorio:

- `logs\`: registros de cada inicio, logs de y-cruncher y registros `AMD_ACPI`
- `profiles\`: historial CO, estado de recuperación, perfiles CO / CS, datos de calibración, ajustes y copias de memoria de BIOS
- `dram\`: carpeta predeterminada para importar / exportar perfiles de OC de memoria (los presets incluidos también están aquí)

## Aviso de riesgos

Tanto PBO como el overclocking de memoria afectan a la estabilidad del sistema. Una configuración incorrecta puede provocar errores de cálculo, bloqueos, pantallas azules, reinicios, pérdida de datos sin guardar o pantalla negra sin arranque (requiriendo limpiar CMOS en escritorio o desconectar la batería principal y la pila CMOS en portátiles). Operar inestable durante mucho tiempo puede dañar el hardware y afectar la garantía. **Guarde sus datos antes de probar, no use el PC durante la prueba y revise todos los valores objetivo antes de escribir en la BIOS.**

Este programa se distribuye bajo la GNU General Public License v3.0 (GPL-3.0) **SIN GARANTÍA ALGUNA**. El autor no asume responsabilidad por pérdida de datos, daños de hardware u otros perjuicios directos o indirectos; el usuario asume todo riesgo.

## Compilación desde el código fuente

```powershell
dotnet publish .\RyzenPboStudio\RyzenPboStudio.csproj -c Release -r win-x64 `
  --self-contained false `
  -p:DebugType=None -p:DebugSymbols=false `
  -o .\memoc
```

La compilación se guarda en `memoc\` y toda la carpeta puede distribuirse directamente (no es archivo único). Si `y-cruncher v0.8.7.9547b\` o `dram-presets\` existen en la raíz del repositorio, se copian automáticamente a `tools\y-cruncher\` y `dram\`; si no, se omite este paso.

## Autor y agradecimientos

- **Desarrollador**: [@lengmo23](https://github.com/lengmo23) ([@冷漠OC](https://v.douyin.com/6VGnQBTxwIQ/))
- **Codesarrollador (OC de memoria)**: [@纯爱出演小猪](https://v.douyin.com/yMpmAidKqdY/) (Cat-girl-memoryOC)
- **Agradecimientos especiales**: `@古腾古腾咕咚咚` (orientación y sugerencias), `@阿肝` (ideas y pruebas), `@Yee` (pruebas principales)

Copyright © 2026 [@lengmo23](https://github.com/lengmo23)

## Invítame a un café

AMD Ryzen PBO Studio es una herramienta de código abierto que desarrollo en mi tiempo libre, afinando cada función a lo largo de muchas iteraciones. Si te ha ahorrado tiempo ajustando tu equipo o te ha ayudado a sacar más rendimiento, ¡puedes invitarme a un café!

El patrocinio es totalmente voluntario y no afecta a ninguna función. Los mismos códigos de pago también están en el botón de la taza de café en la esquina inferior izquierda.

<p align="center">
  <img src="Alipay.png" alt="Alipay" height="400">
  <img src="Wechat.png" alt="WeChat Pay" height="400">
</p>

## Licencia

Este proyecto se publica bajo la [GNU General Public License v3.0](LICENSE). Al enlazar estáticamente ZenStates-Core (también GPL-3.0), todo el proyecto debe distribuirse bajo GPL-3.0.

## Componentes de terceros

- [ZenStates-Core](https://github.com/irusanov/ZenStates-Core) (GPL-3.0): biblioteca de acceso SMU de Ryzen para leer y escribir CO, CS, PM Table y PBO
- [SMUDebugTool](https://github.com/irusanov/SMUDebugTool) (GPL-3.0): referencia de comandos SMU y estructura de PM Table
- [ryzen-smu-cli](https://github.com/rawhide-kobayashi/ryzen-smu-cli) (GPL-3.0): proporciona `inpoutx64.dll`
- [y-cruncher](https://www.numberworld.org/y-cruncher/): motor de pruebas de estrés, copyright Alexander J. Yee
- [PawnIO](https://pawnio.eu): controlador de kernel obligatorio instalado por separado por el usuario

Consulte [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) para los avisos completos.
