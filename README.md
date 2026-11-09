<div align="center">

<img src="docs/icon.png" width="96" alt="Claude Graft para Windows">

<h1>Claude Graft para Windows</h1>

<p><b>Un inicio de sesión de Claude nunca es la cantidad justa.</b><br>
Todas tus cuentas de Claude, a la vez, cada una en su propio perfil.</p>

</div>

Claude Graft para Windows es una versión para Windows de
[Claude Graft](https://github.com/aaditya-v-more/claude-graft), de Aaditya More,
que en macOS vive en la barra de menús. Aquí vive en el área de notificación:
ejecuta varias copias de Claude Desktop una al lado de la otra, cada una con
su propia sesión, sus chats y sus ajustes, y te dice cuánto uso le queda a
cada cuenta.

## Instalación

Necesitas Windows 10 1809 o posterior, [Claude Desktop](https://claude.ai/download)
(la versión de Microsoft Store o la del instalador clásico) y el
[.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0),
salvo que uses la compilación autocontenida.

Descarga `ClaudeGraft.exe` de Releases, ponlo en una carpeta tuya (por ejemplo
`%LOCALAPPDATA%\Programs\ClaudeGraft`) y ábrelo. Como no está firmado,
SmartScreen puede pedirte que confirmes la primera vez (**Más información ›
Ejecutar de todas formas**).

Para compilarlo tú:

```powershell
.\build.ps1              # pruebas + dist\ClaudeGraft.exe
.\build.ps1 -SelfContained -Zip
```

## Un Claude para cada cuenta

**Nuevo Claude** crea un perfil en `%LOCALAPPDATA%\ClaudeGraft\Profiles\<carpeta>`
y, si quieres, accesos directos en el escritorio y en el menú Inicio con su
propio icono y color. Inicias sesión una vez y se queda iniciada. Cada Claude
tiene sus propios chats, servidores MCP y ajustes; con **Copiar servidores MCP
del principal**, en el menú **⋯** de su tarjeta, le añades los que tiene tu Claude de siempre, sin tocar los que
ya tuviera.

Abrir un Claude que ya está abierto lo trae al frente: nunca se arrancan dos
procesos sobre el mismo perfil, porque los dos escribirían los mismos archivos
de chat. Un acceso directo de un Claude que ya has quitado no abre ningún otro
en su lugar.

## Artefactos, proyectos y chats de todas las cuentas

En la barra lateral, **Artefactos**, **Proyectos** y **Buscar chats** reúnen lo
de todas tus cuentas en un sitio. Cada cuenta inicia sesión una vez en
claude.ai, en una ventana de Claude Graft, con el código que llega por correo,
y esa sesión vive en un navegador propio de esa cuenta: Claude Graft nunca lee
su cookie. Los artefactos se pueden abrir, compartir con un enlace, con
personas o con un grupo (cada persona con su permiso de ver, comentar o
editar), renombrar, duplicar, eliminar y exportar a Markdown, PDF, Word o su
código fuente. La búsqueda encuentra chats y sesiones de Code, y solo pregunta
a claude.ai al pulsar Intro o Buscar.

## Uso compartido

Cuando una cuenta se agota, Claude Code sigue con la cuota de otra sin salir
del Claude en el que estás. En su apartado **Uso compartido**, **Añadir otra cuenta** suma otros Claudes, o
inicia sesión con otra cuenta que solo preste su cuota. Solo las respuestas del
modelo salen de la otra cuenta: tus chats, archivos y conectores se quedan
donde están, y la pestaña Chat nunca se comparte.

Responde la cuenta cuya semana se reinicia antes y que aún tiene margen; una
conversación se queda en la cuenta que la empezó mientras dura su caché, y
desde el menú del área de notificación puedes mover cualquier conversación
reciente a otra cuenta. Funciona con un pequeño relé en `127.0.0.1:47823`
con un certificado que solo puede responder por `api.anthropic.com`; Claude
Graft lo pone en marcha y lo para, y configura Claude Code cuando ese Claude
está cerrado, así que los cambios se aplican la próxima vez que se abre.

## Compartir chats

Un Claude nuevo empieza con sus propios chats. En **Lee los chats de** puedes hacer que
lea los de tu Claude principal o los de otro Claude: recibe su propia copia de
esos chats, y cada vez que se abre cualquiera de los dos los cambios viajan en
ambos sentidos (un chat nuevo, uno renombrado, uno borrado). Sus propios chats
se añaden también a los del otro, y esa fusión es lo único que no se puede
deshacer; volver a **Sus propios chats** lo deja exactamente con los suyos,
después de entregarle al otro lo que hiciste mientras tanto. Funciona entre
dos perfiles de la misma cuenta y entre cuentas distintas.

Los chats de Code fijados, su orden y cómo se ordenan también se sincronizan
entre los dos, justo antes de abrir uno, siempre que todos los que comparten
esos chats estén cerrados.

Si abres un Claude mientras otro con los mismos chats está abierto, Claude
Graft te avisa antes, porque abrir la misma conversación en dos a la vez puede
perder mensajes. Y si al cambiar de cuenta dentro de un Claude sus chats se
quedaron en otro perfil, la ventana te dice dónde están y te deja copiarlos.

## El área de notificación

Un clic en el icono abre un panel, como el menú del original: cada Claude con
su uso y un botón **Abrir**, las conversaciones del uso compartido, los
interruptores de **Iniciar con Windows** y del uso en vivo, y las acciones. El
clic derecho abre un menú corto. La ventana sigue el modelo del original: los
Claudes en una barra lateral y, a la derecha, el elegido en tarjetas.


El icono toma el color del Claude que tienes delante y muestra el uso de su
cuenta: el porcentaje de la ventana de cinco horas, o el de la semana (con una
raya debajo) en las cuentas que solo tienen límite semanal. El menú lista todos
los Claudes, cuáles están abiertos y el uso de cada uno, con el tiempo que
falta para que se reinicie en la información emergente. Un clic abre o trae al
frente cualquiera de ellos.

Las cifras salen del historial que cada Claude escribe mientras está abierto
(`plan-usage-history.json`). Si activas **Leer el uso en vivo desde
Anthropic**, se consultan además en el mismo servicio que usa Claude Code, y
están al día aunque ese Claude lleve días cerrado.

## Actualizaciones

Claude Graft mira cada hora si hay una versión nueva de Claude Desktop, con un
identificador propio y sin usar ninguna cuenta, y lo dice abajo en la ventana
y en el panel del icono. Con **Actualizar Claude Desktop manualmente** (en
Ajustes) ningún Claude se actualiza solo, y por tanto ninguna actualización
cierra trabajo a medias; la versión nueva se instala desde esa línea, con
todos los Claudes cerrados (Claude Graft nunca cierra uno por ti), tras
comprobar su suma SHA-256. La versión de Microsoft Store la actualiza la Store.

Claude Graft se actualiza solo desde las
[versiones de este repositorio](https://github.com/MideasDev/claude-graft-windows/releases):
descarga el zip, comprueba su SHA-256, cambia el ejecutable y se reinicia, sin
avisar, salvo que actives **Retener las actualizaciones de Claude Graft**.

Para publicar una versión: sube el número en `Directory.Build.props`, haz commit
y empuja una etiqueta con ese número (`git tag v0.2.0` y `git push origin v0.2.0`).
GitHub compila, pasa las pruebas y publica el zip con su `.sha256`.

## Seguro por diseño

Claude Graft solo toca lo que ha creado: sus perfiles, sus iconos y los
accesos directos que él mismo ha hecho. El perfil propio de Claude nunca se
renombra, se mueve ni se borra; quitar un Claude manda su carpeta a la
Papelera de reciclaje, nunca la borra sin más, y solo si está dentro de la
carpeta de perfiles. Una lista que no se puede leer no se sobrescribe.

El uso en vivo toma prestado el inicio de sesión de cada perfil sin quedárselo:
descifra solo el token de acceso, con la protección de tu propia cuenta de
Windows (DPAPI), nunca el de renovación, no escribe nada de vuelta y lo envía
únicamente a `api.anthropic.com`. Está desactivado hasta que lo activas.

## Diferencias con la versión de macOS

Esta versión trae casi todo lo del original: varios Claudes a la
vez, accesos directos, el uso de cada cuenta en el área de notificación y abrir
sin duplicar. Algunas partes del original dependen de macOS o son delicadas de
portar, y no están todavía; [docs/PORTING.md](docs/PORTING.md) explica cuáles,
por qué y cómo se hizo cada pieza en Windows:

- sincronizar la barra lateral y gestionar las actualizaciones de Claude.

Windows agrupa en la barra de tareas todas las ventanas de Claude bajo un mismo
icono, porque todas son la misma aplicación; el icono del área de notificación
dice cuál tienes delante.

## Ayuda

`ClaudeGraft.exe --report` escribe `%LOCALAPPDATA%\ClaudeGraft\state-report.txt`
con lo que ve el programa (dónde está Claude, qué Claudes están abiertos, las
cifras de uso), y `diagnostics.log`, en la misma carpeta, guarda lo que ha
hecho. Ninguno de los dos contiene tokens ni chats.

[MIT](LICENSE). Basado en [Claude Graft](https://github.com/aaditya-v-more/claude-graft)
de Aaditya More; si te resulta útil, su [bote de propinas](https://ko-fi.com/aadityavmore)
mantiene vivo el original.
