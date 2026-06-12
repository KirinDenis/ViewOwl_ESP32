idf.py fullclean
idf.py set-target esp32
idf.py build -DCMAKE_EXPORT_COMPILE_COMMANDS=ON

REM clang-tidy main/main.c -p build/