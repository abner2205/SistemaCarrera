---
name: feature
description: >-
  Flujo simplificado para cambios rápidos, ajustes puntuales o correcciones que no ameritan una especificación formal SDD de gran escala.
---

# Skill: Feature Rápida

Utiliza este flujo cuando el usuario solicite un cambio puntual, ajuste de UI menor, refactorización pequeña o corrección de bugs evidentes.

---

## Procedimiento

1. **Comprensión**: Analiza el archivo afectado en `src/CarreraApp/` y la petición concreta del usuario.
2. **Impacto**: Verifica si el cambio afecta la base de datos `carrera.db` o la estabilidad de los hilos de WPF.
3. **Implementación**: Realiza la modificación necesaria en el código.
4. **Verificación**:
   - Ejecuta `dotnet build SistemaCarrera.sln`.
   - Si existen pruebas afectadas, ejecuta `dotnet test`.
5. **Reporte**: Explica brevemente qué se modificó y confirma que la compilación y pruebas están en verde.
6. **Sincronización con GitHub**:
   - Solicita aprobación rápida al usuario para publicar los cambios.
   - Crea el commit siguiendo la convención (`fix: ...` o `feat: ...`).
   - Ejecuta `git push origin <rama>` para subir el cambio aprobado a GitHub.

