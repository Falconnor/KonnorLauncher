# Pruebas automáticas de Konnor Launcher

Proyecto: `Launcher/tests/KonnorLauncher.Core.Tests`. Hay **206 pruebas**, que prueban la librería `KonnorLauncher.Core`. Las que necesitan red usan un servidor HTTP local (`Infrastructure/LocalHttpServer.cs`) y carpetas temporales (`Infrastructure/TempDir.cs`), así que no usan Internet ni tocan la instalación real.

Para ejecutarlas, desde la carpeta `Launcher`:

```
dotnet test -c Release
```

Las pruebas con varios casos (`[Theory]`) se cuentan una vez por caso: cada caso es una prueba de las 206.

## Resumen por sector

| Sector | Archivo | Código probado | Pruebas |
|---|---|---|---:|
| [1. Configuración](#1-configuración) | `ConfigurationTests.cs` | `Core/Configuration` | 26 |
| [2. Instancias](#2-instancias) | `InstancesTests.cs` | `Core/Instances`, `Core/Loaders` | 43 |
| [3. Sincronización con el servidor](#3-sincronización-con-el-servidor) | `InstanceSyncTests.cs` | `Core/Instances`, `Core/Verification` | 7 |
| [4. Galería de skins (MineSkin)](#4-galería-de-skins-mineskin) | `MineSkinTests.cs` | `Core/Players` | 10 |
| [5. Modrinth (Explorar)](#5-modrinth-explorar) | `ModrinthTests.cs` | `Core/Modrinth` | 15 |
| [6. Paquetes de recursos y shaders](#6-paquetes-de-recursos-y-shaders) | `PacksTests.cs` | `Core/Verification` | 6 |
| [7. Rutas, jugador, Java y versiones](#7-rutas-jugador-java-y-versiones) | `PathsAndPlayersTests.cs` | `Core/Paths`, `Core/Players`, `Core/Launching`, `Core/Versions` | 27 |
| [8. Juego definido por el servidor](#8-juego-definido-por-el-servidor) | `ServerGameTests.cs` | `Core/Verification`, `Core/Modrinth` | 11 |
| [9. Texturas de skin](#9-texturas-de-skin) | `SkinTextureTests.cs` | `Core/Players` | 8 |
| [10. Aviso de actualizaciones](#10-aviso-de-actualizaciones) | `UpdateCheckTests.cs` | `Core/Instances` | 9 |
| [11. Verificación de archivos](#11-verificación-de-archivos) | `VerificationTests.cs` | `Core/Verification` | 44 |
| **Total** | | | **206** |

---

## 1. Configuración

`ConfigurationTests.cs`: lectura y escritura de `launcher.properties` y de su vista tipada (ajustes).

| # | Prueba | Qué comprueba |
|---:|---|---|
| 1 | `Loads_original_python_properties` | Lee un `launcher.properties` del launcher original en Python. |
| 2 | `Set_rewrites_only_the_key_line_and_keeps_comments_and_order` | Cambiar un valor reescribe solo su línea; se conservan los comentarios y el orden. |
| 3 | `Set_appends_missing_key_at_the_end` | Una clave que no existe se añade al final. |
| 4 | `Missing_file_is_empty_and_is_created_on_save` | Sin archivo, la configuración está vacía y el archivo se crea al guardar. |
| 5 | `Parsing_is_tolerant_like_python` | Acepta líneas raras (espacios, sin `=`…) igual que el original. |
| 6 | `ParseBool_only_accepts_literal_true` · `"true"` → sí | Solo «true» (sin distinguir mayúsculas) es verdadero. |
| 7 | `ParseBool_only_accepts_literal_true` · `"TRUE"` → sí | Ídem. |
| 8 | `ParseBool_only_accepts_literal_true` · `" True "` → sí | Ídem, con espacios. |
| 9 | `ParseBool_only_accepts_literal_true` · `"false"` → no | Ídem. |
| 10 | `ParseBool_only_accepts_literal_true` · `"1"` → no | «1» no cuenta como verdadero. |
| 11 | `ParseBool_only_accepts_literal_true` · `"yes"` → no | «yes» no cuenta como verdadero. |
| 12 | `ParseBool_only_accepts_literal_true` · `""` → no | Vacío es falso. |
| 13 | `ParseBool_only_accepts_literal_true` · `null` → no | Sin valor es falso. |
| 14 | `ParseMemoryMb_converts_xmx_format` · `"4G"` → 4096 | Convierte la memoria con formato `-Xmx` a MB. |
| 15 | `ParseMemoryMb_converts_xmx_format` · `"4g"` → 4096 | Ídem, en minúscula. |
| 16 | `ParseMemoryMb_converts_xmx_format` · `"16G"` → 16384 | Ídem. |
| 17 | `ParseMemoryMb_converts_xmx_format` · `"512M"` → 512 | Ídem, en MB. |
| 18 | `ParseMemoryMb_converts_xmx_format` · `"2048"` → 2048 | Un número sin unidad se toma en MB. |
| 19 | `ParseMemoryMb_converts_xmx_format` · `"4 GB"` → 4096 | Acepta «GB» con espacio. |
| 20 | `ParseMemoryMb_converts_xmx_format` · `"abc"` → nada | Texto no válido. |
| 21 | `ParseMemoryMb_converts_xmx_format` · `"0G"` → nada | Cero no es válido. |
| 22 | `ParseMemoryMb_converts_xmx_format` · `""` → nada | Vacío no es válido. |
| 23 | `FormatMemory_round_trips` · 4096 → `"4G"` | Escribe la memoria en formato `-Xmx`. |
| 24 | `FormatMemory_round_trips` · 1024 → `"1G"` | Ídem. |
| 25 | `FormatMemory_round_trips` · 1536 → `"1536M"` | Si no son GB enteros, se escribe en MB. |
| 26 | `Settings_defaults_match_python` | Los valores por defecto de los ajustes son los del launcher original. |

## 2. Instancias

`InstancesTests.cs`: crear, guardar y cargar instancias; nombres; versiones de los cargadores y memoria.

| # | Prueba | Qué comprueba |
|---:|---|---|
| 27 | `Slugify_makes_safe_folder_names` · «Survival con amigos» | El nombre se convierte en un nombre de carpeta seguro (`survival-con-amigos`). |
| 28 | `Slugify_makes_safe_folder_names` · «  Técnico Ñandú 1.21  » | Quita tildes, eñes y espacios (`tecnico-nandu-1-21`). |
| 29 | `Slugify_makes_safe_folder_names` · «!!!» | Un nombre sin letras ni números da `instancia`. |
| 30 | `Slugify_makes_safe_folder_names` · nombre muy largo | Se recorta a 32 caracteres. |
| 31 | `Invalid_names_are_rejected` · `""` | Rechaza un nombre vacío. |
| 32 | `Invalid_names_are_rejected` · `"   "` | Rechaza un nombre de solo espacios. |
| 33 | `Invalid_names_are_rejected` · 41 caracteres | Rechaza un nombre demasiado largo. |
| 34 | `Create_assigns_unique_ids_and_creates_the_folder` | Cada instancia nueva recibe un id único y su carpeta. |
| 35 | `Save_and_load_round_trip_every_field` | Todos los campos se guardan y se vuelven a leer igual. |
| 36 | `LoadAll_orders_by_last_played_and_skips_damaged_files` | La lista se ordena por la última partida y salta los `instance.json` dañados. |
| 37 | `Delete_removes_the_folder_and_rejects_ids_outside_the_store` | Borrar elimina la carpeta y rechaza ids que apuntan fuera de `instances`. |
| 38 | `Legacy_properties_become_the_first_instance_only_once` | La configuración antigua (sin instancias) se convierte en la primera instancia, una sola vez. |
| 39 | `Fresh_install_without_version_or_verification_creates_nothing` | En una instalación nueva sin versión ni servidor no se crea ninguna instancia. |
| 40 | `Loader_version_ids_are_understood` · `fabric-loader-0.19.3-1.21.1` | Interpreta el id de versión: Minecraft 1.21.1, Fabric 0.19.3. |
| 41 | `Loader_version_ids_are_understood` · `quilt-loader-0.20.0-beta.9-1.20.1` | Minecraft 1.20.1, Quilt 0.20.0-beta.9. |
| 42 | `Loader_version_ids_are_understood` · `1.20.1-forge-47.3.0` | Minecraft 1.20.1, Forge 47.3.0. |
| 43 | `Loader_version_ids_are_understood` · `1.12.2-forge1.12.2-14.23.5.2859` | Formato antiguo de Forge. |
| 44 | `Loader_version_ids_are_understood` · `neoforge-21.1.77` | NeoForge 21.1.77 → Minecraft 1.21.1. |
| 45 | `Loader_version_ids_are_understood` · `neoforge-20.4.237` | NeoForge 20.4.237 → Minecraft 1.20.4. |
| 46 | `Loader_version_ids_are_understood` · `1.21.1` | Versión vanilla. |
| 47 | `Loader_version_ids_are_understood` · `26.3` | Vanilla con la numeración nueva de Minecraft. |
| 48 | `Synced_instance_summary_shows_the_server_loader` | El resumen de una instancia del servidor muestra el cargador que publica el servidor. |
| 49 | `Memory_text_is_understood` · `"6"` → 6144 | Un número suelto se entiende en GB. |
| 50 | `Memory_text_is_understood` · `"6 GB"` → 6144 | Ídem, con unidad. |
| 51 | `Memory_text_is_understood` · `"6,5"` → 6656 | Acepta decimales con coma. |
| 52 | `Memory_text_is_understood` · `"6.5g"` → 6656 | Acepta decimales con punto. |
| 53 | `Memory_text_is_understood` · `"6144"` → 6144 | Un número grande se entiende en MB. |
| 54 | `Memory_text_is_understood` · `"6144 MB"` → 6144 | Ídem, con unidad. |
| 55 | `Memory_text_is_understood` · `"512m"` → 512 | MB en minúscula. |
| 56 | `Memory_text_is_understood` · `"abc"` → nada | Texto no válido. |
| 57 | `Memory_text_is_understood` · `""` → nada | Vacío. |
| 58 | `Memory_text_is_understood` · `"-2"` → nada | Negativo. |
| 59 | `Memory_is_formatted_and_clamped` | La memoria se muestra con formato y se limita al mínimo y al máximo. |
| 60 | `Instance_memory_is_saved_and_null_means_the_global_setting` | La memoria propia se guarda; sin valor se usa la de Opciones. |
| 61 | `Normal_instances_share_meta_and_synced_ones_keep_their_own_files` | Las instancias normales comparten librerías y assets; las del servidor tienen los suyos. |
| 62 | `Server_version_prefers_the_loader_over_vanilla` | Al detectar la versión del servidor se prefiere la del cargador antes que la vanilla. |
| 63 | `Standalone_loader_json_is_detected_by_its_main_class` | Reconoce el JSON de un cargador por su clase principal. |
| 64 | `Server_version_without_loader_is_the_newest_vanilla` | Sin cargador, se toma la vanilla más reciente. |
| 65 | `Server_without_versions_has_no_version` | Si el servidor no tiene versiones, no se toma ninguna. |
| 66 | `Fabric_list_uses_the_stable_flag` | En la lista de Fabric se usa su indicador de versión estable. |
| 67 | `Quilt_betas_are_not_stable` | Las betas de Quilt no cuentan como estables. |
| 68 | `Without_stable_versions_stable_falls_back_to_the_newest` | Si no hay versiones estables, se usa la más reciente. |
| 69 | `Empty_or_unexpected_json_gives_an_empty_list` | Un JSON vacío o inesperado da una lista vacía, sin errores. |

## 3. Sincronización con el servidor

`InstanceSyncTests.cs`: política de seguridad del servidor (vínculo, liberación y «siempre en línea»).

| # | Prueba | Qué comprueba |
|---:|---|---|
| 70 | `Successful_sync_links_the_instance_and_remembers_the_policy` | Una sincronización correcta vincula la instancia y guarda la política del servidor. |
| 71 | `Older_servers_without_policy_keep_the_current_behaviour` | Los servidores antiguos, sin política, mantienen el comportamiento de siempre. |
| 72 | `Released_server_unlocks_the_instance_for_good` | Un servidor liberado desbloquea la instancia para siempre. |
| 73 | `Url_lock_follows_the_server_and_is_reversible` | El bloqueo de la dirección sigue lo que diga el servidor y se puede deshacer. |
| 74 | `New_instances_never_lock_the_url_before_their_first_sync` | Una instancia nueva no bloquea la dirección antes de su primera sincronización. |
| 75 | `Unreachable_server_is_reported_as_such` | Si no se puede conectar con el servidor, se informa así. |
| 76 | `Manifest_policy_flags_are_parsed` | Se leen las opciones de política del manifest. |

## 4. Galería de skins (MineSkin)

`MineSkinTests.cs`: cliente de la API de MineSkin.org, contra un servidor HTTP local.

| # | Prueba | Qué comprueba |
|---:|---|---|
| 77 | `List_ReadsSkinsAndNextCursor_AndDropsInvalidTextures` | Lee la lista de skins y el cursor de la página siguiente; descarta las texturas no válidas. |
| 78 | `List_WithoutNext_HasNoCursor` | En la última página no hay cursor. |
| 79 | `Get_ReadsVariantAndTexture` | Lee el modelo (clásico o delgado) y la textura de una skin. |
| 80 | `Get_Missing_ThrowsReadableError` | Una skin que no existe da un error legible. |
| 81 | `DownloadTexture_SavesFile` | La textura se descarga y se guarda en disco. |
| 82 | `DownloadTexture_RejectsInvalidHash_WithoutRequest` | Un hash no válido se rechaza sin hacer ninguna petición. |
| 83 | `IsTextureHash` · hash real → sí | Acepta un hash de textura válido. |
| 84 | `IsTextureHash` · `"ABC"` → no | Rechaza mayúsculas y hashes demasiado cortos. |
| 85 | `IsTextureHash` · `"a3b8/../x"` → no | Rechaza rutas (evita salir de la carpeta). |
| 86 | `IsTextureHash` · `null` → no | Rechaza un valor vacío. |

## 5. Modrinth (Explorar)

`ModrinthTests.cs`: búsqueda e instalación de contenido de Modrinth, contra un servidor HTTP local.

| # | Prueba | Qué comprueba |
|---:|---|---|
| 87 | `Search_facets_combine_groups_with_and_and_options_with_or` | Los filtros se combinan bien: grupos con Y, opciones de un grupo con O. |
| 88 | `Search_results_are_read_from_the_api` | Lee los resultados de búsqueda de la API. |
| 89 | `Installing_a_mod_brings_its_required_dependencies_and_remembers_them` | Instalar un mod trae sus dependencias obligatorias y las registra. |
| 90 | `A_new_version_replaces_the_one_installed_before` | Una versión nueva sustituye a la instalada antes. |
| 91 | `Downloads_with_a_wrong_hash_or_unsafe_name_are_rejected` | Rechaza descargas con un hash incorrecto o un nombre de archivo inseguro. |
| 92 | `Synced_and_vanilla_instances_do_not_accept_mods` | Las instancias del servidor y las vanilla no aceptan mods. |
| 93 | `Data_packs_go_into_the_chosen_world` | Los data packs se instalan en el mundo elegido. |
| 94 | `A_chosen_version_is_installed_even_if_it_is_not_the_latest` | Se instala la versión elegida aunque no sea la última. |
| 95 | `Project_details_are_read_with_body_gallery_and_links` | La página de un proyecto se lee con su descripción, su galería y sus enlaces. |
| 96 | `A_modpack_becomes_a_new_instance_with_its_loader_files_and_overrides` | Un modpack crea una instancia nueva con su cargador, sus archivos y sus `overrides`. |
| 97 | `Modpacks_cannot_write_outside_the_instance_or_replace_its_files` · `../fuera.txt` | Un modpack no puede escribir fuera de la instancia. |
| 98 | `Modpacks_cannot_write_outside_the_instance_or_replace_its_files` · `C:/Windows/evil.dll` | Ni en rutas absolutas. |
| 99 | `Modpacks_cannot_write_outside_the_instance_or_replace_its_files` · `instance.json` | Ni sustituir archivos del launcher. |
| 100 | `Overrides_cannot_escape_the_instance_either` | Los `overrides` del modpack tampoco pueden salir de la instancia. |
| 101 | `Only_https_or_local_downloads_are_allowed` | Solo se permiten descargas por HTTPS (o locales). |

## 6. Paquetes de recursos y shaders

`PacksTests.cs`: paquetes de recursos y de shaders, opcionales para el jugador o protegidos por el servidor.

| # | Prueba | Qué comprueba |
|---:|---|---|
| 102 | `Without_the_switch_packs_are_not_installed_and_the_players_are_kept` | Con el interruptor apagado no se instalan paquetes y se conservan los del jugador. |
| 103 | `With_the_switch_only_missing_packs_are_installed` | Con el interruptor encendido solo se instalan los que faltan. |
| 104 | `Protected_packs_are_forced_repaired_and_cleaned_like_the_rest` | Los paquetes protegidos se instalan siempre, se reparan y se limpian como los demás archivos. |
| 105 | `The_lock_follows_the_server_and_the_switch_stays_on` | El bloqueo sigue lo que diga el servidor y el interruptor se queda encendido. |
| 106 | `Older_servers_do_not_protect_packs` | Los servidores antiguos no protegen paquetes. |
| 107 | `Pack_paths_are_recognised_by_their_first_folder` | Un paquete se reconoce por la primera carpeta de su ruta. |

## 7. Rutas, jugador, Java y versiones

`PathsAndPlayersTests.cs`: rutas del juego, nombre de jugador, UUID sin conexión, Java, versiones instaladas y skins.

| # | Prueba | Qué comprueba |
|---:|---|---|
| 108 | `Empty_game_path_resolves_to_official_minecraft` | Sin ruta del juego se usa la carpeta oficial `.minecraft`. |
| 109 | `Relative_game_path_is_relative_to_launcher_dir` | Una ruta relativa parte de la carpeta del launcher. |
| 110 | `Game_sub_dirs_are_relative_to_game_dir_or_absolute` | Las subcarpetas del juego son relativas a él o absolutas. |
| 111 | `Username_regex` · `Steve` → válido | Nombre de jugador válido. |
| 112 | `Username_regex` · `abc_123` → válido | Acepta guion bajo y números. |
| 113 | `Username_regex` · `abcd` → válido | Mínimo de 4 caracteres. |
| 114 | `Username_regex` · `abcdefghijklmnop` → válido | Máximo de 16 caracteres. |
| 115 | `Username_regex` · `abc` → no válido | Demasiado corto. |
| 116 | `Username_regex` · `abcdefghijklmnopq` → no válido | Demasiado largo. |
| 117 | `Username_regex` · `con espacio` → no válido | Sin espacios. |
| 118 | `Username_regex` · `ñandú` → no válido | Sin eñes ni tildes. |
| 119 | `Username_regex` · `""` → no válido | Vacío. |
| 120 | `Options_messages_are_identical_to_python` | Los mensajes de Opciones son iguales a los del launcher original. |
| 121 | `Prompt_messages_are_identical_to_python` | Los mensajes de las preguntas son iguales a los del launcher original. |
| 122 | `Offline_uuid_matches_java_nameUUIDFromBytes` | El UUID sin conexión coincide con el que calcula Java (`nameUUIDFromBytes`). |
| 123 | `Session_respects_manual_uuid_and_token` | La sesión respeta un UUID y un token puestos a mano. |
| 124 | `Generic_java_values_use_mojang_runtime` · `""` | Sin Java configurado se usa el Java de Mojang. |
| 125 | `Generic_java_values_use_mojang_runtime` · `java` | «java» genérico usa el Java de Mojang. |
| 126 | `Generic_java_values_use_mojang_runtime` · `JAVAW.EXE` | Ídem, en mayúsculas. |
| 127 | `Generic_java_values_use_mojang_runtime` · `java.exe` | Ídem. |
| 128 | `Existing_relative_java_is_relative_to_game_dir` | Una ruta relativa de Java que existe parte de la carpeta del juego. |
| 129 | `Missing_java_falls_back_to_mojang_runtime` | Si el Java configurado no existe, se usa el de Mojang. |
| 130 | `Installed_versions_match_python_rules_and_order` | Las versiones instaladas se detectan y ordenan como en el original. |
| 131 | `Combined_list_puts_installed_first_without_duplicates` | La lista combinada pone primero las instaladas, sin duplicados. |
| 132 | `Missing_versions_dir_returns_empty` | Sin carpeta `versions`, la lista está vacía. |
| 133 | `Classic_skin_mirrors_right_limbs` | En las skins antiguas las extremidades izquierdas son el espejo de las derechas. |
| 134 | `Hat_layer_is_drawn_over_the_head` | La capa del sombrero se dibuja sobre la cabeza. |

## 8. Juego definido por el servidor

`ServerGameTests.cs`: manifest con la versión del juego. El launcher instala el juego y descarga de Modrinth los archivos que llevan SHA-512; el resto, del servidor.

| # | Prueba | Qué comprueba |
|---:|---|---|
| 135 | `Parser_reads_the_game_and_the_sha512` | Lee del manifest la versión del juego y los SHA-512. |
| 136 | `Parser_rejects_an_invalid_game` · cargador desconocido | Rechaza un cargador que no existe (p. ej. «optifine»). |
| 137 | `Parser_rejects_an_invalid_game` · Fabric sin versión | Rechaza un cargador sin versión. |
| 138 | `Parser_rejects_an_invalid_game` · `../1.21.1` | Rechaza una versión con ruta. |
| 139 | `Parser_rejects_an_invalid_game` · `"1.21.1"` suelto | Rechaza un formato que no es un objeto. |
| 140 | `Mods_on_Modrinth_come_from_there_and_the_rest_from_the_server` | Los mods que están en Modrinth se bajan de allí; el resto, del servidor. |
| 141 | `A_wrong_or_missing_file_on_Modrinth_falls_back_to_the_server` | Si Modrinth da un archivo incorrecto o no lo tiene, se baja del servidor. |
| 142 | `If_Modrinth_does_not_answer_everything_comes_from_the_server` | Si Modrinth no responde, todo se baja del servidor. |
| 143 | `Files_already_in_place_do_not_ask_Modrinth` | No se consulta Modrinth por archivos que ya están bien. |
| 144 | `Sync_adopts_the_version_and_loader_of_the_manifest` | Al sincronizar, la instancia toma la versión y el cargador del manifest. |
| 145 | `Modrinth_lookup_returns_the_file_with_the_requested_hash` | La búsqueda en Modrinth devuelve el archivo con el hash pedido. |

## 9. Texturas de skin

`SkinTextureTests.cs`: validación de archivos de skin y textura para el visor 3D.

| # | Prueba | Qué comprueba |
|---:|---|---|
| 146 | `Skin_sizes_are_accepted` · 64×64 | Acepta el tamaño moderno. |
| 147 | `Skin_sizes_are_accepted` · 64×32 | Acepta el tamaño antiguo. |
| 148 | `Other_sizes_are_rejected_with_the_actual_size` · 128×128 | Rechaza otros tamaños y dice el tamaño real. |
| 149 | `Other_sizes_are_rejected_with_the_actual_size` · 32×32 | Ídem. |
| 150 | `Other_sizes_are_rejected_with_the_actual_size` · 64×48 | Ídem. |
| 151 | `Files_that_are_not_png_are_rejected` | Rechaza archivos que no son PNG. |
| 152 | `Classic_skins_get_mirrored_left_limbs_and_opaque_base` | Las skins antiguas reciben extremidades izquierdas en espejo y una capa base opaca. |
| 153 | `Slim_skins_are_detected_by_the_unused_arm_columns` | Las skins delgadas (Alex) se detectan por las columnas vacías de los brazos. |

## 10. Aviso de actualizaciones

`UpdateCheckTests.cs`: consulta de los servidores al abrir el launcher y aviso de actualización disponible.

| # | Prueba | Qué comprueba |
|---:|---|---|
| 154 | `Client_versions_compare_by_parts` · 1.0.10 > 1.0.9 | Las versiones se comparan por partes, no como texto. |
| 155 | `Client_versions_compare_by_parts` · 1.0 = 1.0.0 | Los ceros finales no cambian la versión. |
| 156 | `Client_versions_compare_by_parts` · 2.0 < 10.0 | Comparación numérica. |
| 157 | `Client_versions_compare_by_parts` · 1.1-beta = 1.1-Beta | No distingue mayúsculas. |
| 158 | `Client_versions_compare_by_parts` · 1.2 > 1.1.9 | Comparación por partes. |
| 159 | `Only_a_later_version_is_an_update` | Solo una versión posterior cuenta como actualización. |
| 160 | `Opening_the_launcher_detects_updates_without_downloading_anything` | Al abrir el launcher se detectan las actualizaciones sin descargar nada. |
| 161 | `The_version_only_counts_as_installed_after_a_successful_sync` | Una versión solo cuenta como instalada después de una sincronización correcta. |
| 162 | `Unreachable_servers_or_bad_urls_are_skipped` | Los servidores inaccesibles o las direcciones mal escritas se saltan sin error. |

## 11. Verificación de archivos

`VerificationTests.cs`: el verificador que compara el cliente con el manifest del servidor y descarga, repara y limpia archivos. Se prueba contra un servidor HTTP local.

| # | Prueba | Qué comprueba |
|---:|---|---|
| 163 | `Case01_empty_client_downloads_everything_and_second_run_repairs_nothing` | Un cliente vacío lo descarga todo; una segunda pasada no repara nada. |
| 164 | `Case02_file_with_different_size_is_downloaded_again` | Un archivo con otro tamaño se descarga de nuevo. |
| 165 | `Case02b_same_size_file_is_assumed_valid` | Un archivo con el mismo tamaño se da por bueno (verificación rápida). |
| 166 | `Case03_wrong_hash_fails_without_cleanup_or_partial_file` | Un hash incorrecto da error sin limpiar nada ni dejar archivos a medias. |
| 167 | `Case04_server_down_reports_servidor_no_responde` | Con el servidor caído se informa «servidor no responde». |
| 168 | `Case04b_invalid_json_reports_servidor_no_responde` | Un manifest con JSON no válido también informa «servidor no responde». |
| 169 | `Case05_verify_disabled_succeeds_without_network` | Con la verificación desactivada funciona sin red. |
| 170 | `Case06_dangerous_paths_are_rejected_before_touching_disk` · `../x` | Rechaza rutas peligrosas antes de tocar el disco: subir de carpeta. |
| 171 | ídem · `a/../../x` | Subir de carpeta desde dentro. |
| 172 | ídem · `C:/x` | Unidad absoluta. |
| 173 | ídem · `/abs` | Ruta absoluta. |
| 174 | ídem · `a//b` | Separadores dobles. |
| 175 | ídem · `./a` | Segmento `.`. |
| 176 | ídem · `mods/a.jar:stream` | Flujos alternativos de NTFS. |
| 177 | ídem · `mods/con?.jar` | Caracteres no válidos en Windows. |
| 178 | ídem · `mods/fin.` | Nombre que termina en punto. |
| 179 | ídem · `\\server\share\x` | Ruta de red. |
| 180 | `Case07_cleanup_removes_unlisted_jar_and_keeps_saves` | La limpieza borra los `.jar` que no están en el manifest y conserva los mundos. |
| 181 | `Case07b_cleanup_disabled_keeps_everything` | Con la limpieza desactivada se conserva todo. |
| 182 | `Case07c_server_decision_overrides_the_launcher` | Lo que decide el servidor sobre la limpieza manda sobre el ajuste del launcher. |
| 183 | `Case07d_server_can_disable_the_cleanup` | El servidor puede desactivar la limpieza. |
| 184 | `Case08_manifest_without_asset_blocks_works` | Funciona con un manifest sin bloques de assets. |
| 185 | `Case09_dry_run_counts_without_changing_disk` | En modo de prueba cuenta los cambios sin tocar el disco. |
| 186 | `Case10_spaces_and_non_ascii_are_encoded_like_python_quote` | Los espacios y caracteres no ASCII de las direcciones se codifican igual que en el original. |
| 187 | `Missing_file_on_server_fails_and_reports_client_version` | Si falta un archivo en el servidor, da error e indica la versión del cliente. |
| 188 | `Official_minecraft_dir_skips_cleanup_by_default` | En la carpeta oficial `.minecraft` no se limpia por defecto. |
| 189 | `Partial_download_is_resumed_with_range` | Una descarga a medias se reanuda (cabecera `Range`). |
| 190 | `Corrupt_partial_is_discarded_and_downloaded_again` | Una descarga a medias dañada se descarta y se baja de nuevo. |
| 191 | `Cut_connection_is_resumed_on_retry` | Si se corta la conexión, se reanuda al reintentar. |
| 192 | `Cut_block_download_is_resumed_and_extracted` | Un bloque cortado se reanuda y se extrae. |
| 193 | `Server_without_range_support_downloads_the_whole_file` | Si el servidor no admite reanudar, se baja el archivo entero. |
| 194 | `Identical_files_in_different_paths_do_not_share_a_partial` | Archivos idénticos en rutas distintas no comparten la descarga a medias. |
| 195 | `Empty_file_is_created` | Se crean los archivos vacíos. |
| 196 | `Many_blocks_are_downloaded_and_extracted_in_parallel` | Muchos bloques se descargan y extraen en paralelo. |
| 197 | `Corrupt_block_fails_without_cleanup` | Un bloque dañado da error sin limpiar nada. |
| 198 | `Manifest_without_files_object_is_rejected` · `{}` | Rechaza un manifest sin `files`. |
| 199 | `Manifest_without_files_object_is_rejected` · `[]` | Rechaza un manifest que no es un objeto. |
| 200 | `Manifest_without_files_object_is_rejected` · `{"files": []}` | Rechaza `files` que no es un objeto. |
| 201 | `Invalid_sizes_are_rejected` · `true` | Rechaza tamaños que no son números. |
| 202 | `Invalid_sizes_are_rejected` · `1.5` | Rechaza tamaños con decimales. |
| 203 | `Invalid_sizes_are_rejected` · `-1` | Rechaza tamaños negativos. |
| 204 | `Invalid_sizes_are_rejected` · `"10"` | Rechaza tamaños escritos como texto. |
| 205 | `Sha_is_normalized_to_upper_case` | El SHA se pasa a mayúsculas antes de comparar. |
| 206 | `Backslashes_are_normalized` | Las barras invertidas de las rutas se convierten en `/`. |
