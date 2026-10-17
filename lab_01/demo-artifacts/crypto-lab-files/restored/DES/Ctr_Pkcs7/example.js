// Пример исходного кода JavaScript: статистика массива.
function statistics(values) {
    if (values.length === 0) throw new Error("Пустой массив");
    const sum = values.reduce((total, value) => total + value, 0);
    return { min: Math.min(...values), max: Math.max(...values), mean: sum / values.length };
}

console.log(statistics([-10, -3, 0, 4, 9, 15]));
