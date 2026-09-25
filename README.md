# RAMMap Tray Companion

Adaptador y monitor en la bandeja del sistema (System Tray) para Windows con interfaz Fluent Design y optimización de memoria a bajo nivel.

---

## Autor

- **Morales Venegas, David Joel**

---

## Descripción

Aplicación en C# .NET y WPF que proporciona acceso rápido y visualización del estado de la memoria RAM desde el área de notificación de Windows. Incorpora integración nativa con rutinas del kernel de Windows NT y sirve como complemento interactivo para la herramienta Sysinternals RAMMap de Microsoft.

### Funcionalidades:
- **Icono Dinámico en Bandeja:** Dibuja el porcentaje de memoria en uso directamente sobre el icono del System Tray en tiempo real.
- **Flyout con Estilo Windows 11:** Panel desplegable con diseño Fluent Dark Mode (WinUI 3) que detalla memoria total, usada, en espera (Standby) y libre.
- **Optimización Nativa vía NT API:** Ejecuta llamadas directas a `ntdll.dll` (`NtSetSystemInformation`) con elevación de privilegios (`SeProfileSingleProcessPrivilege`, `SeIncreaseQuotaPrivilege`) para purgar:
  - Lista de espera (Standby List).
  - Conjuntos de trabajo de procesos de usuario (Working Sets).
  - Lista de páginas modificadas (Modified Page List).
- **Lanzador de Sysinternals RAMMap:** Detecta y restaura instancias en ejecución o abre el binario oficial de RAMMap si se encuentra en el entorno o rutas del sistema.

---

## Estructura del Repositorio

```text
rammap-tray/
├── src/
│   ├── App.xaml / App.xaml.cs                 # Punto de entrada, mutex de instancia única y ciclo de vida
│   ├── FlyoutWindow.xaml / .cs                # Ventana emergente con paleta Windows 11 Dark Mode
│   ├── NativeMemory.cs                        # P/Invoke a kernel32 y ntdll para purga de memoria
│   ├── TrayIconManager.cs                     # Generación dinámica del icono en GDI+ y menú contextual
│   ├── app.manifest                           # Manifiesto de privilegios y compatibilidad DPI
│   └── RAMMapTray.csproj                      # Configuración del proyecto .NET
├── scripts/
│   ├── ConfigStartWindows.bat                 # Crea una tarea programada para inicio con Windows sin UAC
│   └── RunRAMMapTray.bat                      # Lanzador con elevación automática de permisos
├── .gitignore                                 # Reglas de exclusión para .NET y binarios externos
└── README.md
```

---

## Requisitos y Compilación

- **Sistema Operativo:** Windows 10 (1809+) o Windows 11 (x64).
- **SDK:** .NET SDK 8.0, 9.0 o 10.0 con soporte para Windows Desktop / WPF.

### Compilación desde terminal:
```bash
cd src
dotnet build -c Release
```

### Publicación autocontenida (Single-File):
```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

El ejecutable resultante puede colocarse junto a los scripts en la carpeta `scripts/` o registrarse para iniciar con el sistema ejecutando `ConfigStartWindows.bat` como Administrador.

---

## Nota sobre Sysinternals RAMMap

Los ejecutables oficiales `RAMMap.exe` / `RAMMap64.exe` son propiedad de Microsoft Corporation. Esta herramienta actúa como un companion/adaptador complementario y no redistribuye binarios propietarios de Microsoft. Si desea abrir la interfaz completa de RAMMap desde el menú contextual, puede descargar el ejecutable oficial desde [Microsoft Learn Sysinternals](https://learn.microsoft.com/sysinternals/downloads/rammap) y colocarlo en la misma carpeta o en el PATH del sistema.
