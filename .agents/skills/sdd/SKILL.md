---
name: sdd
description: >-
  Metodología Spec-Driven Development (SDD) para el desarrollo estructurado de funcionalidades mediante especificación formal, clarificación de QA, planificación incremental e implementación validada tarea por tarea.
---

# Skill: Spec-Driven Development (SDD)

Esta habilidad guía el proceso formal de desarrollo de características en SistemaCarrera para evitar ambigüedades y retrabajos.

---

## 1. El Flujo SDD

```
[Petición Usuario]
        │
        ▼
   Fase 1: Spec (Planner)
        │
        ▼
   Fase 2: Clarificación (Reviewer QA) ──► Aprobación del Usuario
        │
        ▼
   Fase 3: Plan y Tareas (Planner) ──────► Aprobación del Usuario
        │
        ▼
   Fase 4: Implementación (Implementer: T1, T2...) ──► dotnet test en verde
        │
        ▼
   Fase 5: Validación RF por RF (Reviewer) ──► VEREDICTO: APROBADO
        │
        ▼
   Fase 6: Cierre, Memoria y Push a GitHub (Coordinator)
        │     - Actualizar MEMORY.md
        │     - Git commit (convención feat/fix)
        │     - git push origin <rama>
        ▼
   [Entrega Finalizada en GitHub]
```

---

## 2. Requisitos Funcionales en Sintaxis EARS

Cada Requisito Funcional (**RF-01**, **RF-02**, etc.) en `spec.md` debe redactarse usando la sintaxis **EARS** (*Easy Approach to Requirements Syntax*):

- **Requisito Ubicuo** (siempre activo):
  `El sistema DEBERÁ <respuesta del sistema>.`
- **Requisito Guiado por Evento**:
  `CUANDO <evento disparador>, el sistema DEBERÁ <respuesta del sistema>.`
- **Requisito Guiado por Estado**:
  `MIENTRAS <estado específico>, el sistema DEBERÁ <respuesta del sistema>.`
- **Comportamiento No Deseado / Error**:
  `SI <condición de error/excepción>, ENTONCES el sistema DEBERÁ <respuesta de recuperación>.`
- **Característica Opcional**:
  `DONDE <característica opcional esté habilitada>, el sistema DEBERÁ <respuesta>.`

---

## 3. Plantillas de Archivos

### A. Plantilla `specs/NNN-nombre/spec.md`
```markdown
# Especificación: NNN - [Nombre de la Funcionalidad]

**Estado:** Borrador | En Revisión | Aprobado
**Fecha:** AAAA-MM-DD

## 1. Propósito y Contexto
- **Qué**: Descripción funcional de la característica.
- **Por qué**: Problema u oportunidad que resuelve para la carrera o los administradores.

## 2. Requisitos Funcionales (EARS)
- **RF-01**: CUANDO [evento], el sistema DEBERÁ [acción].
- **RF-02**: MIENTRAS [estado], el sistema DEBERÁ [acción].
- **RF-03**: SI [error], ENTONCES el sistema DEBERÁ [recuperación].

## 3. Casos Límite y Restricciones
- Concurrencia o accesos simultáneos.
- Valores fuera de rango (tiempos negativos, cadenas vacías, duplicados).
- Comportamiento ante fallos de red o base de datos SQLite.

## 4. Criterios de Aceptación
- [ ] Criterio 1
- [ ] Criterio 2
```

### B. Plantilla `specs/NNN-nombre/plan.md`
```markdown
# Plan de Implementación: NNN - [Nombre de la Funcionalidad]

**Spec de referencia:** `specs/NNN-nombre/spec.md`

## 1. Arquitectura y Componentes Afectados
- Modelos (`Models/`):
- Persistencia (`Data/`):
- Lógica / Utilidades (`Helpers/`):
- Servidor / Endpoints (`Server/` / `wwwroot/`):
- Vistas (`UI/` / XAML):

## 2. Decisiones Técnicas y Alternativas Descartadas
- **Decisión 1**: [Explicación y por qué se descartó la alternativa X]

## 3. Estrategia de Pruebas
- Pruebas unitarias en `tests/`:
- Verificación manual / UI:

## 4. Matriz de Cobertura de RF
- RF-01 -> Componentes / Métodos / Tests que lo cubren.
- RF-02 -> Componentes / Métodos / Tests que lo cubren.
```

### C. Plantilla `specs/NNN-nombre/tasks.md`
```markdown
# Tareas de Implementación: NNN - [Nombre de la Funcionalidad]

**Plan:** `specs/NNN-nombre/plan.md`

- [ ] **T1: [Nombre de la tarea]**
  - **RF Asociados:** RF-01
  - **Archivos:** `Ruta/Al/Archivo.cs`
  - **Hecho cuando:** `dotnet build` compila y el test unitario `TestXYZ` pasa en verde.

- [ ] **T2: [Nombre de la tarea]**
  - **RF Asociados:** RF-02
  - **Archivos:** `Ruta/Al/Archivo.xaml`, `Ruta/Al/Archivo.xaml.cs`
  - **Hecho cuando:** [Criterio objetivo y comprobable].
```

