Servicio de Windows que revisa automáticamente los pull requests de Bitbucket Cloud.

## Aviso sobre el proveedor de LLM

El diff de cada pull request se envía al proveedor de LLM configurado en la sección
`Llm` de `appsettings.json`. Esto significa que **el contenido del código revisado
viaja fuera de la infraestructura de Bitbucket**.

Por seguridad, conviene elegir un proveedor:

- **sin retención de prompts** (que no guarde los mensajes para reentrenamiento ni
  para analítica), o
- **local / on-premise** (un modelo autoalojado al que no llegue código de terceros).

Consulta la política de uso de datos del proveedor antes de activarlo en un
repositorio con código sensible.