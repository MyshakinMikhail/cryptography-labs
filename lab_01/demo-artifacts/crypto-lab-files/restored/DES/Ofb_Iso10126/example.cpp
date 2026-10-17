// Пример исходного кода C++: сортировка вставками.
#include <iostream>
#include <vector>

int main() {
    std::vector<int> values{9, -3, 4, 0, 15, -10};
    for (std::size_t i = 1; i < values.size(); ++i) {
        const int value = values[i];
        std::size_t j = i;
        while (j > 0 && values[j - 1] > value) {
            values[j] = values[j - 1];
            --j;
        }
        values[j] = value;
    }
    for (int value : values) std::cout << value << ' ';
    std::cout << '\n';
}
