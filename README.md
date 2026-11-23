# Monitor de Bateria

Aplicativo de bandeja para Windows 10/11 que encontra os dispositivos sem fio do PC e mostra
o tipo, a marca e quanto de bateria cada um tem, se está carregando ou descarregando.

## Como usar

- Rode `dist\MonitorBateria.exe` (gere com `.\publicar.ps1`).
- O app fica na **área de notificação** (perto do relógio). Se não aparecer, clique na setinha `^`
  e arraste o ícone para a barra, ou ative em *Configurações › Personalização › Barra de tarefas ›
  Outros ícones da bandeja do sistema*.
- **Clique esquerdo** no ícone abre/fecha o **painel lateral**; **clique direito** abre o menu.
- No painel:
  - cada card mostra ícone do tipo, marca, nível, barra colorida e estado
    (carregando / descarregando / estável, com estimativa de tempo);
  - fones TWS mostram cada fone separado (Fone 1 / Fone 2);
  - botão 🖥 = ícone próprio desse dispositivo na barra (com o número da bateria);
  - botão 👁 = ocultar; os ocultos ficam em "Ocultos" no fim da lista;
  - clique direito no card: renomear, usar no ícone principal etc.
- ⚙ Configurações: **monitor** onde o painel abre (principal, onde está o mouse, Monitor 1, 2…),
  **lado** (direita/esquerda), fechar ao clicar fora, intervalo de atualização, alerta de bateria
  fraca, iniciar com o Windows e a lista de todos os dispositivos encontrados.

Argumentos: `--tray` (inicia só na bandeja, usado pelo "Iniciar com o Windows"),
`--settings` (abre direto nas configurações), `--dump [arquivo]` (gera um relatório de diagnóstico).

## De onde vem a bateria

| Fonte | Dispositivos |
|---|---|
| Propriedade de bateria Bluetooth do Windows (a mesma de *Configurações › Bluetooth*) | fones, headsets, mouses, teclados, controles BT clássico e LE |
| Serviço de bateria GATT (0x180F) | outros BLE conectados que o Windows não reporta |
| Logitech HID++ 2.0 (mesmo protocolo do G HUB, funciona junto com ele) | mouses/teclados em receptor LIGHTSPEED, Unifying ou Bolt; headsets com dongle próprio (G533, G733, G935, PRO X…) |
| XInput | controles Xbox via cabo ou adaptador Xbox Wireless (nível aproximado em 4 faixas) |
| Bateria do sistema | notebook/tablet |

Limitações: dongles de outras marcas (Razer HyperSpeed, Corsair Slipstream, SteelSeries, HyperX…)
usam protocolos próprios e ainda não são lidos. Headsets Logitech informam tensão, não porcentagem,
então o valor é aproximado (~). Com o headset desligado o dongle continua respondendo e o card
mostra "Desligado ou fora de alcance".
A maioria dos dispositivos Bluetooth não informa se está carregando; o app deduz pelo aumento/queda
do nível ao longo do tempo. Alguns teclados (ex.: Keychron) não reportam bateria nenhuma.

Configurações ficam em `%APPDATA%\MonitorBateria\settings.json`.

## Estrutura

```
src/MonitorBateria/
  Models/     BatteryDevice (estado + inferência de carga), DeviceClassifier (tipo/marca/ícone)
  Services/   Logitech/ (HidPpChannel = HID++ sobre HID, LogitechProvider), BluetoothProvider, GattBatteryReader, XInputProvider, SystemBatteryProvider,
              DeviceMonitor (agrega/atualiza/alerta), TrayManager + TrayIconRenderer,
              AppSettings, StartupManager
  UI/         SidePanel (painel lateral + configurações), diálogos
tools/make-icon.ps1   gera Assets/app.ico
```
